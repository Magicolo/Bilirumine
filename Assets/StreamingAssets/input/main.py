import random, sys, ast, threading, torch, asyncio, os, pickle, mmap
from queue import SimpleQueue, Empty

sys.path.append("/comfy")
import server, execution
from nodes import (
    ImageBatch,
    CLIPTextEncode,
    CheckpointLoaderSimple,
    KSampler,
    NODE_CLASS_MAPPINGS,
    LoadImage,
    VAEEncode,
    EmptyImage,
    ImageScale,
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


def work(receive: SimpleQueue, steps):
    global PAUSE, CANCEL, WAIT

    tasks = SimpleQueue()
    while True:
        try:
            wait = None if tasks.empty() else WAIT
            (state, *inputs) = receive.get(block=True, timeout=wait)
            tasks.put((state, inputs, steps(state, *inputs)), block=False)
        except Empty:
            pass

        for _ in range(tasks.qsize()):
            (state, inputs, task) = tasks.get(block=False)
            while True:
                if state["version"] in CANCEL:
                    break
                elif state["version"] in PAUSE:
                    tasks.put((state, inputs, task), block=False)
                    break
                else:
                    outputs = next(task)
                    if outputs is None:
                        continue
                    yield state, inputs, outputs
                    break


def reserve(size: int):
    global NEXT, CAPACITY, GENERATION, MEMORY_LOCK

    with MEMORY_LOCK:
        if NEXT + size > CAPACITY:
            offset, NEXT = 0, 0
            GENERATION += 1
        else:
            offset = NEXT
        NEXT += size

        return offset, GENERATION


def load(state: dict, memory: mmap.mmap = None):
    global GENERATION

    if state["size"] and state["generation"] == GENERATION and memory:
        memory.seek(state["offset"])
        data = memory.read(state["size"])
        loaded = torch.frombuffer(data, dtype=torch.uint8)
        loaded = loaded.to(dtype=torch.float32)
        loaded = loaded / 255.0
        loaded = loaded.reshape(1, *state["shape"], 3)
    elif state["load"]:
        (loaded, _) = LoadImage().load_image(state["load"])
    elif state["empty"]:
        (loaded,) = EmptyImage().generate(state["width"], state["height"], 1, 0)
    else:
        loaded = None
    return loaded


def read(send):
    global PAUSE, CANCEL

    with open(MEMORY, "r+b") as file:
        with mmap.mmap(file.fileno(), CAPACITY, access=mmap.ACCESS_READ) as memory:
            with torch.inference_mode():
                (model, clip, vae) = CheckpointLoaderSimple().load_checkpoint(
                    "dreamshaperXL_v21TurboDPMSDE.safetensors"
                )
                while True:
                    state = {
                        **ast.literal_eval(input()),
                        "model": model,
                        "clip": clip,
                        "vae": vae,
                    }
                    if state["cancel"]:
                        CANCEL = CANCEL.union(state["cancel"])
                    if state["pause"] or state["resume"]:
                        PAUSE = PAUSE.union(state["pause"]).difference(state["resume"])
                    loaded = load(state, memory)
                    if loaded is None:
                        continue
                    send.put((state, loaded), block=False)


def extend(receive: SimpleQueue, send: SimpleQueue):
    def steps(state, loaded):
        yield None
        # Apply random variation to outpaint to reduce recurring patterns.
        left = nudge(state["left"], 0.25)
        top = nudge(state["top"], 0.25)
        right = nudge(state["right"], 0.25)
        bottom = nudge(state["bottom"], 0.25)
        zoom = nudge(state["zoom"], 0.25)
        (scaled,) = scaler.upscale(
            loaded, "bicubic", state["width"], state["height"], "disabled"
        )

        if zoom > 0 or left > 0 or top > 0 or right > 0 or bottom > 0:
            (cropped,) = cropper.crop(
                width=state["width"] - zoom * 2 - left - right,
                height=state["height"] - zoom * 2 - top - bottom,
                x=zoom + right,
                y=zoom + bottom,
                image=scaled,
            )
            yield None
        else:
            cropped = scaled

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
            (positive,) = clipper.encode(text=state["positive"], clip=state["clip"])
            yield None
            (negative,) = clipper.encode(text=state["negative"], clip=state["clip"])
            yield None
            (positive, negative, encoded) = encoder.encode(
                positive, negative, padded, state["vae"], mask
            )
            yield None
            (sampled,) = sampler.sample(
                seed=seed(),
                steps=5,
                cfg=1.0,
                sampler_name="lcm",
                scheduler="sgm_uniform",
                denoise=1.0,
                model=state["model"],
                positive=positive,
                negative=negative,
                latent_image=encoded,
            )
            yield None
            (decoded,) = decoder.decode(samples=sampled, vae=state["vae"])
            yield None
        else:
            decoded = cropped

        if zoom > 0:
            (zoomed,) = scaler.upscale(
                decoded, "bicubic", state["width"], state["height"], "disabled"
            )
            yield None
        else:
            zoomed = decoded

        yield (scaled, zoomed)

    with torch.inference_mode():
        clipper = CLIPTextEncode()
        encoder = NODE_CLASS_MAPPINGS["InpaintModelConditioning"]()
        decoder = VAEDecode()
        cropper = NODE_CLASS_MAPPINGS["ImageCrop"]()
        padder = ImagePadForOutpaint()
        scaler = ImageScale()
        sampler = KSampler()
        for state, _, (scaled, zoomed) in work(receive, steps):
            send.put((state, scaled, zoomed), block=False)


def detail(
    receive: SimpleQueue, send: SimpleQueue, loop: SimpleQueue, end: SimpleQueue
):

    def steps(state: dict, _, zoomed):
        yield None
        (positive,) = clipper.encode(text=state["positive"], clip=state["clip"])
        yield None
        (negative,) = clipper.encode(text=state["negative"], clip=state["clip"])
        yield None
        (encoded,) = encoder.encode(state["vae"], zoomed)
        yield None
        (sampled,) = sampler.sample(
            seed=seed(),
            steps=state["steps"],
            cfg=state["guidance"],
            sampler_name="euler_ancestral",
            scheduler="sgm_uniform",
            denoise=state["denoise"],
            model=state["model"],
            positive=positive,
            negative=negative,
            latent_image=encoded,
        )
        yield None
        (decoded,) = decoder.decode(samples=sampled, vae=state["vae"])
        yield (decoded,)

    with torch.inference_mode():
        clipper = CLIPTextEncode()
        encoder = VAEEncode()
        decoder = VAEDecode()
        sampler = KSampler()
        for state, (scaled, _), (decoded,) in work(receive, steps):
            if state["full"]:
                send.put((state, scaled, decoded), block=False)
            else:
                end.put((state, decoded), block=False)

            if state["next"]:
                next = {**state, **state["next"]}
                loaded = load(next)
                loop.put((next, decoded if loaded is None else loaded), block=False)
            elif state["loop"]:
                loop.put((state, decoded), block=False)


def interpolate(receive: SimpleQueue, send: SimpleQueue):
    def steps(state, scaled, decoded):
        yield None
        (batched,) = batcher.batch(scaled, decoded)
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
            multiplier=18,
            fast_mode=True,
            ensemble=True,
            scale_factor=1,
        )
        yield (images,)

    with torch.inference_mode():
        batcher = ImageBatch()
        interpolator = NODE_CLASS_MAPPINGS["RIFE VFI"]()
        for state, _, (images,) in work(receive, steps):
            send.put((state, images[1:]), block=False)


def write(receive: SimpleQueue):

    def steps(state, images):
        yield None
        [count, height, width, _] = images.shape
        images = images * 255.0
        yield None
        images = images.clamp(0.0, 255.0)
        yield None
        images = images.to(dtype=torch.uint8)
        yield None
        images = images.numpy()
        yield None
        data = images.tobytes()
        yield None
        size = len(data)
        offset, generation = reserve(size)
        yield None
        memory.seek(offset)
        memory.write(data)
        yield None
        output(
            f'{state["version"]},{state["tags"]},{width},{height},{count},{offset},{size},{generation}'
        )
        yield None
        torch.cuda.empty_cache()
        yield (True,)

    with open(MEMORY, "r+b") as file:
        with mmap.mmap(file.fileno(), CAPACITY, access=mmap.ACCESS_WRITE) as memory:
            with torch.inference_mode():
                for _, _, _ in work(receive, steps):
                    pass


WAIT = 0.1
PAUSE = set()
CANCEL = set()
CLIPS = {}
GENERATION = 1
CAPACITY = 2**31 - 1
NEXT = 0
MEMORY = "/dev/shm/bilirumine"
MEMORY_LOCK = threading.Lock()
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
    threading.Thread(target=detail, args=(b, c, a, d)),
    threading.Thread(target=interpolate, args=(c, d)),
    threading.Thread(target=write, args=(d,)),
]
for thread in threads:
    thread.start()
for thread in threads:
    thread.join()
