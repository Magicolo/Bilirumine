import random, sys, json, threading, torch, base64, asyncio, numpy
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
    return value * (random.random() * by + 1.0)


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


def read(send):
    global STATE

    with torch.inference_mode():
        generator = EmptyImage()
        while True:
            state = json.loads(input())
            state = {**STATE, **state}
            if state["run"] and not STATE["run"]:
                (image,) = generator.generate(
                    state["width"], state["height"], batch_size=1, color=0
                )
                send.put((state, image), block=False)
            STATE = state


def extend(receive: SimpleQueue, send: SimpleQueue):
    global STATE

    with torch.inference_mode():
        loader = CheckpointLoaderSimple()
        clip_encoder = CLIPTextEncode()
        vae_encoder = VAEEncodeForInpaint()
        vae_decoder = VAEDecode()
        cropper = NODE_CLASS_MAPPINGS["ImageCrop"]()
        padder = ImagePadForOutpaint()
        scaler = ImageScale()
        sampler = KSampler()
        (
            model,
            clip,
            vae,
        ) = loader.load_checkpoint("dreamshaperXL_lightningInpaint.safetensors")
        (positive,) = clip_encoder.encode(text=STATE["positive"], clip=clip)
        (negative,) = clip_encoder.encode(
            text=STATE["negative"],
            clip=clip,
        )
        while True:
            (state, loaded) = receive.get(block=True)
            # Apply random variation to outpaint to reduce recurring patterns.
            left = nudge(state["left"], 0.25)
            top = nudge(state["top"], 0.25)
            right = nudge(state["right"], 0.25)
            bottom = nudge(state["bottom"], 0.25)

            (cropped,) = cropper.crop(
                width=int(state["width"] - state["zoom"] * 2 - left - right),
                height=int(state["height"] - state["zoom"] * 2 - top - bottom),
                x=int(state["zoom"] + right),
                y=int(state["zoom"] + bottom),
                image=loaded,
            )
            (padded, mask) = padder.expand_image(
                cropped,
                int(left),
                int(top),
                int(right),
                int(bottom),
                int(min(state["width"], state["height"]) / 4),
            )
            (encoded,) = vae_encoder.encode(vae, padded, mask, 0)
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
            (decoded,) = vae_decoder.decode(samples=sampled, vae=vae)
            (scaled,) = scaler.upscale(
                decoded,
                "nearest-exact",
                state["width"],
                state["height"],
                "disabled",
            )
            send.put((state, loaded, scaled), block=False)


def detail(receive: SimpleQueue, send: SimpleQueue, loop: SimpleQueue):
    global STATE

    with torch.inference_mode():
        checkpoint_loader = CheckpointLoaderSimple()
        clip_encoder = CLIPTextEncode()
        vae_encoder = VAEEncode()
        vae_decoder = VAEDecode()
        sampler = KSampler()
        batcher = ImageBatch()
        (model, clip, vae) = checkpoint_loader.load_checkpoint(
            "dreamshaperXL_v21TurboDPMSDE.safetensors"
        )
        (positive,) = clip_encoder.encode(text=STATE["positive"], clip=clip)
        (negative,) = clip_encoder.encode(
            text=STATE["negative"],
            clip=clip,
        )
        while True:
            (state, loaded, decoded) = receive.get(block=True)
            (encoded,) = vae_encoder.encode(vae, decoded)
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
            (decoded,) = vae_decoder.decode(samples=sampled, vae=vae)
            (batched,) = batcher.batch(loaded, decoded)
            send.put((state, batched), block=False)
            loop.put((STATE, decoded), block=False)


def interpolate(receive: SimpleQueue, send: SimpleQueue):
    global STATE

    with torch.inference_mode():
        interpolator = NODE_CLASS_MAPPINGS["RIFE VFI"]()
        while True:
            (state, batched) = receive.get(block=True)
            (interpolated,) = interpolator.vfi(
                ckpt_name="rife49.pth",
                frames=batched,
                clear_cache_after_n_frames=100,
                multiplier=6,
                fast_mode=True,
                ensemble=True,
                scale_factor=0.25,
            )
            (interpolated,) = interpolator.vfi(
                ckpt_name="rife49.pth",
                frames=interpolated,
                clear_cache_after_n_frames=100,
                multiplier=8,
                fast_mode=True,
                ensemble=True,
                scale_factor=1,
            )
            send.put((state, interpolated), block=False)


def write(receive: SimpleQueue):
    global STATE

    with torch.inference_mode():
        while True:
            (_, interpolated) = receive.get(block=True)
            images = interpolated[:-1]  # Skip last frame such that it is not repeated.
            for image in images:
                image = image.numpy().flatten()
                image = numpy.clip(image * 255.0, 0, 255).astype(numpy.uint8)
                response = base64.b64encode(image.tobytes()).decode("utf-8")
                output(response)
            torch.cuda.empty_cache()


# NOTE: All prompts must be pre-encoded; the 'Clip' model is too large to keep in memory.
STATE = {
    "run": False,
    "width": 512,
    "height": 512,
    "zoom": 0,
    "left": 0,
    "right": 0,
    "top": 0,
    "bottom": 0,
    "positive": "(ultra detailed, oil painting, abstract, conceptual, hyper realistic, vibrant) Everything is a 'TCHOO TCHOO' train. Flesh organic locomotive speeding on vast empty nebula tracks. Eternal spiral railways in the cosmos. Coal ember engine of intricate fusion. Unholy desecrated church station. Runic glyphs neon 'TCHOO' engravings. Darkness engulfed black hole pentagram. Blood magic eldritch rituals to summon whimsy hellish trains of wonder. Everything is a 'TCHOO TCHOO' train.",
    "negative": "(blurry, worst quality, low detail, monochrome, simple, centered)",
}
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
