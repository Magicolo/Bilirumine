import random, sys, ast, threading, torch, base64, asyncio, numpy, os, pickle, traceback, mmap
from queue import SimpleQueue

sys.path.append("/comfy")
import server, execution
from nodes import (
    ImageBatch,
    CLIPTextEncode,
    CheckpointLoaderSimple,
    KSampler,
    NODE_CLASS_MAPPINGS,
    VAEEncode,
    EmptyImage,
    ImageScale,
    VAEEncodeForInpaint,
    ImagePadForOutpaint,
    VAEDecode,
    init_custom_nodes,
)


def seed():
    return random.randint(1, 2**64)


def nudge(value, by):
    return int(value * (random.random() * by + 1.0))


def input():
    with INPUT_LOCK:
        return sys.stdin.readline()


def output(message):
    with OUTPUT_LOCK:
        sys.stdout.write(f"{message}\n")
        sys.stdout.flush()


def error(message):
    with ERROR_LOCK:
        sys.stderr.write(f"{message}\n")
        sys.stderr.flush()


def encode(image):
    image = image.numpy().flatten()
    image = numpy.clip(image * 255.0, 0, 255).astype(numpy.uint8)
    image = base64.b64encode(image.tobytes()).decode("utf-8")
    return image


def decode(image, width, height):
    image = base64.b64decode(image)
    image = numpy.frombuffer(image, dtype=numpy.uint8)
    image = image.astype(numpy.float32) / 255.0
    image = image.reshape(1, height, width, 4)
    image = torch.tensor(image[:, :, :, 1:])
    return image


def clipped(name, prompt, clip, cache):
    def encode(prompt, clip):
        with torch.inference_mode():
            encoder = CLIPTextEncode()
            (encoded,) = encoder.encode(text=prompt, clip=clip)
            return encoded

    name = f"{name}-{hash(prompt)}.clip"
    if cache:
        path = os.path.join(cache, name)
        if os.path.exists(path):
            with open(path, "rb") as file:
                return pickle.load(file)
        encoded = encode(prompt, clip)
        with open(path, "wb") as file:
            pickle.dump(encoded, file)
    else:
        with CLIP_LOCK:
            if name in CLIPS:
                return CLIPS[name]
        encoded = encode(prompt, clip)
        with CLIP_LOCK:
            CLIPS[name] = encoded

    return encoded


def iterate(state: dict, steps):
    while state["version"] >= STOP:
        output = next(steps)
        if output is not None:
            return output


def read(send):
    global IMAGE, STOP, BREAK

    with torch.inference_mode():
        while True:
            try:
                state = {**ast.literal_eval(input()), "loop": 0}
                BREAK = max(BREAK, state["version"])
                STOP = max(STOP, state["version"] * state["stop"])

                if state["skip"]:
                    continue
                elif state["image"]:
                    image = decode(state["image"], state["width"], state["height"])
                elif IMAGE is None:
                    (image,) = EmptyImage().generate(
                        state["width"], state["height"], 1, 0
                    )
                else:
                    image = IMAGE
                send.put((state, image), block=False)
            except:
                error(traceback.format_exc())


def extend(receive: SimpleQueue, send: SimpleQueue):

    def steps(state, loaded):
        yield None
        # Apply random variation to outpaint to reduce recurring patterns.
        left = nudge(state["left"], 0.25)
        top = nudge(state["top"], 0.25)
        right = nudge(state["right"], 0.25)
        bottom = nudge(state["bottom"], 0.25)
        yield None
        (scaled,) = scaler.upscale(
            loaded, "nearest-exact", state["width"], state["height"], "disabled"
        )
        yield None
        (cropped,) = cropper.crop(
            width=state["width"] - state["zoom"] * 2 - left - right,
            height=state["height"] - state["zoom"] * 2 - top - bottom,
            x=state["zoom"] + right,
            y=state["zoom"] + bottom,
            image=scaled,
        )
        yield None
        if left > 0 or top > 0 or right > 0 or bottom > 0:
            (padded, mask) = padder.expand_image(
                cropped,
                left,
                top,
                right,
                bottom,
                min(state["width"], state["height"]) // 4,
            )
            yield None
            (encoded,) = encoder.encode(vae, padded, mask, 0)
            yield None
            positive = clipped("extend", state["positive"], clip, state["cache"])
            yield None
            negative = clipped("extend", state["negative"], clip, state["cache"])
            yield None
            (sampled,) = sampler.sample(
                seed=seed(),
                steps=1,
                cfg=5,
                sampler_name="lcm",
                scheduler="sgm_uniform",
                denoise=1.0,
                model=model,
                positive=positive,
                negative=negative,
                latent_image=encoded,
            )
            yield None
            (decoded,) = decoder.decode(samples=sampled, vae=vae)
            yield None
        else:
            decoded = cropped
        (zoomed,) = scaler.upscale(
            decoded, "nearest-exact", state["width"], state["height"], "disabled"
        )
        yield (scaled, zoomed)

    with torch.inference_mode():
        encoder = VAEEncodeForInpaint()
        decoder = VAEDecode()
        cropper = NODE_CLASS_MAPPINGS["ImageCrop"]()
        padder = ImagePadForOutpaint()
        scaler = ImageScale()
        sampler = KSampler()
        (model, clip, vae) = CheckpointLoaderSimple().load_checkpoint(
            "dreamshaperXL_lightningInpaint.safetensors"
        )
        while True:
            try:
                (state, loaded) = receive.get(block=True)
                (scaled, zoomed) = iterate(state, steps(state, loaded))
                send.put((state, scaled, zoomed), block=False)
            except:
                error(traceback.format_exc())


def detail(receive: SimpleQueue, send: SimpleQueue, loop: SimpleQueue):

    def steps(state: dict, scaled, zoomed):
        yield None
        positive = clipped("detail", state["positive"], clip, state["cache"])
        yield None
        negative = clipped("detail", state["negative"], clip, state["cache"])
        yield None
        (encoded,) = encoder.encode(vae, zoomed)
        yield None
        (sampled,) = sampler.sample(
            seed=seed(),
            steps=5,
            cfg=2.5,
            sampler_name="euler_ancestral",
            scheduler="sgm_uniform",
            denoise=0.55,
            model=model,
            positive=positive,
            negative=negative,
            latent_image=encoded,
        )
        yield None
        (decoded,) = decoder.decode(samples=sampled, vae=vae)
        yield None
        (batched,) = batcher.batch(scaled, decoded)
        yield (batched, decoded)

    with torch.inference_mode():
        encoder = VAEEncode()
        decoder = VAEDecode()
        sampler = KSampler()
        batcher = ImageBatch()
        (model, clip, vae) = CheckpointLoaderSimple().load_checkpoint(
            "dreamshaperXL_v21TurboDPMSDE.safetensors"
        )
        while True:
            try:
                (state, scaled, zoomed) = receive.get(block=True)
                (batched, decoded) = iterate(state, steps(state, scaled, zoomed))
                send.put((state, batched), block=False)
                if state["version"] >= BREAK:
                    next = state if state["next"] is None else state["next"]
                    next = {**next, "loop": state["loop"] + 1}
                    loop.put((next, decoded), block=False)
            except:
                error(traceback.format_exc())


def interpolate(receive: SimpleQueue, send: SimpleQueue):

    def steps(state, batched):
        yield None
        (interpolated,) = interpolator.vfi(
            ckpt_name="rife49.pth",
            frames=batched,
            clear_cache_after_n_frames=100,
            multiplier=6,
            fast_mode=True,
            ensemble=True,
            scale_factor=0.25,
        )
        yield None
        (images,) = interpolator.vfi(
            ckpt_name="rife49.pth",
            frames=interpolated,
            clear_cache_after_n_frames=100,
            multiplier=9,
            fast_mode=True,
            ensemble=True,
            scale_factor=1,
        )
        yield images

    with torch.inference_mode():
        interpolator = NODE_CLASS_MAPPINGS["RIFE VFI"]()
        while True:
            try:
                (state, batched) = receive.get(block=True)
                images = iterate(state, steps(state, batched))
                send.put((state, images), block=False)
            except:
                error(traceback.format_exc())


def write(receive: SimpleQueue):
    global IMAGE

    with open("/dev/shm/bilirumine", "r+b") as file:
        capacity = 1024 * 1024 * 1024
        next = 0
        with mmap.mmap(file.fileno(), capacity) as memory:
            with torch.inference_mode():
                while True:
                    try:
                        (state, images) = receive.get(block=True)
                        images, IMAGE = images[:-1], images[-1:]
                        [count, height, width, _] = images.shape
                        images = images * 255.0
                        images = images.clamp(0.0, 255.0)
                        images = images.to(dtype=torch.uint8)
                        images = images.numpy()
                        data = images.tobytes()
                        size = len(data)

                        if next + size > capacity:
                            offset, next = 0, 0
                        else:
                            offset = next

                        next += size
                        memory.seek(offset)
                        memory.write(data)
                        output(
                            f'{state["version"]},{state["loop"]},{width},{height},{count},{offset},{size}'
                        )
                        torch.cuda.empty_cache()
                    except:
                        error(traceback.format_exc())


IMAGE = None
STOP = 0
BREAK = 0
CLIPS = {}
CLIP_LOCK = threading.Lock()
INPUT_LOCK = threading.Lock()
OUTPUT_LOCK = threading.Lock()
ERROR_LOCK = threading.Lock()
loop = asyncio.new_event_loop()
asyncio.set_event_loop(loop)
instance = server.PromptServer(loop)
execution.PromptQueue(instance)
init_custom_nodes()
a = SimpleQueue()
b = SimpleQueue()
c = SimpleQueue()
d = SimpleQueue()
threads = [
    threading.Thread(target=read, args=(a,)),
    threading.Thread(target=extend, args=(a, b)),
    threading.Thread(target=detail, args=(b, c, a)),
    threading.Thread(target=interpolate, args=(c, d)),
    threading.Thread(target=write, args=(d,)),
]
for thread in threads:
    thread.start()
for thread in threads:
    thread.join()
