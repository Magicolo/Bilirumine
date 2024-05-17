import random, sys, ast, threading, torch, base64, asyncio, numpy, os, pickle
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


def clipped(name, prompt, clip):
    folder = os.path.dirname(os.path.realpath(__file__))
    folder = f"{folder}/.cache/clip"
    path = f"{folder}/{name}_{hash(prompt)}.pkl"
    os.makedirs(folder, exist_ok=True)

    if os.path.exists(path):
        with open(path, "rb") as file:
            return pickle.load(file)

    with torch.inference_mode():
        encoder = CLIPTextEncode()
        (encoded,) = encoder.encode(text=prompt, clip=clip)
        with open(path, "wb") as file:
            pickle.dump(encoded, file)
        return encoded


def iterate(state: dict, steps):
    for output in steps:
        if state["identifier"] < STOP:
            break
        elif output is None:
            continue
        else:
            return output


def read(send):
    global STATE, STOP

    with torch.inference_mode():
        first = True
        while True:
            state = ast.literal_eval(input())
            old = STATE
            new = {**old, **state}
            STATE = new
            if new["stop"] > 0:
                STOP = new["stop"]
            if first:
                first = False
                (image,) = EmptyImage().generate(
                    new["width"], new["height"], batch_size=1, color=0
                )
                send.put((new, image), block=False)


def extend(receive: SimpleQueue, send: SimpleQueue):

    def steps(state, loaded):
        yield None
        positive = clipped("extend", state["positive"], clip)
        yield None
        negative = clipped("extend", state["negative"], clip)
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
            (state, loaded) = receive.get(block=True)
            (scaled, zoomed) = iterate(state, steps(state, loaded))
            send.put((state, scaled, zoomed), block=False)


def detail(receive: SimpleQueue, send: SimpleQueue, loop: SimpleQueue):

    def steps(state: dict, scaled, zoomed):
        yield None
        positive = clipped("detail", state["positive"], clip)
        yield None
        negative = clipped("detail", state["negative"], clip)
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
            (state, scaled, zoomed) = receive.get(block=True)
            (batched, decoded) = iterate(state, steps(state, scaled, zoomed))
            send.put((state, batched), block=False)
            loop.put((STATE, decoded), block=False)


def interpolate(receive: SimpleQueue, send: SimpleQueue):

    def steps(state, batched):
        yield None
        (interpolated,) = interpolator.vfi(
            ckpt_name="rife49.pth",
            frames=batched,
            clear_cache_after_n_frames=100,
            multiplier=5,
            fast_mode=True,
            ensemble=True,
            scale_factor=0.25,
        )
        yield None
        (interpolated,) = interpolator.vfi(
            ckpt_name="rife49.pth",
            frames=interpolated,
            clear_cache_after_n_frames=100,
            multiplier=15,
            fast_mode=True,
            ensemble=True,
            scale_factor=1,
        )
        yield interpolated

    with torch.inference_mode():
        interpolator = NODE_CLASS_MAPPINGS["RIFE VFI"]()
        while True:
            (state, batched) = receive.get(block=True)
            interpolated = iterate(state, steps(state, batched))
            send.put((state, interpolated), block=False)


def write(receive: SimpleQueue):

    def steps(state, interpolated):
        yield None
        images = interpolated[:-1]  # Skip last frame such that it is not repeated.
        for image in images:
            yield None
            image = image.numpy().flatten()
            yield None
            image = numpy.clip(image * 255.0, 0, 255).astype(numpy.uint8)
            yield None
            response = base64.b64encode(image.tobytes()).decode("utf-8")
            yield None
            output(response)
        yield True

    with torch.inference_mode():
        while True:
            (state, interpolated) = receive.get(block=True)
            iterate(state, steps(state, interpolated))
            torch.cuda.empty_cache()


# NOTE: All prompts must be pre-encoded; the 'Clip' model is too large to keep in memory.
STATE = {
    "width": 512,
    "height": 512,
    "zoom": 0,
    "left": 0,
    "right": 0,
    "top": 0,
    "bottom": 0,
    "positive": "(ultra detailed, oil painting, abstract, conceptual, hyper realistic, vibrant) Everything is a 'TCHOO TCHOO' train. Flesh organic locomotive speeding on vast empty nebula tracks. Eternal spiral railways in the cosmos. Coal ember engine of intricate fusion. Unholy desecrated church station. Runic glyphs neon 'TCHOO' engravings. Darkness engulfed black hole pentagram. Blood magic eldritch rituals to summon whimsy hellish trains of wonder. Everything is a 'TCHOO TCHOO' train.",
    "negative": "(nude, naked, child, children, blurry, worst quality, low detail, monochrome, simple, centered)",
}
STOP = 0
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
