import asyncio, sys, threading, torch, asyncio, mmap, utility
from typing import Optional
from queue import SimpleQueue

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


def load(state: dict, memory: Optional[mmap.mmap] = None):
    if memory and state["size"] and state["generation"]:
        data = utility.read(memory, state["offset"], state["size"], state["generation"])
        if data is not None:
            loaded = torch.frombuffer(data, dtype=torch.uint8)
            loaded = loaded.to(dtype=torch.float32)
            loaded = loaded / 255.0
            loaded = loaded.reshape(1, *state["shape"], 3)
            return loaded

    if state["load"]:
        (loaded, _) = LoadImage().load_image(state["load"])
    elif state["empty"]:
        (loaded,) = EmptyImage().generate(state["width"], state["height"], 1, 0)
    else:
        loaded = None
    return loaded


def read(send: SimpleQueue):
    with utility.memory("image", mmap.ACCESS_READ) as memory, torch.inference_mode():
        (model, clip, vae) = CheckpointLoaderSimple().load_checkpoint(
            "dreamshaperXL_v21TurboDPMSDE.safetensors"
        )
        while True:
            state = {**utility.input(), "model": model, "clip": clip, "vae": vae}
            utility.update(state["cancel"], state["pause"], state["resume"])
            loaded = load(state, memory)
            if loaded is None:
                continue
            send.put((state, loaded), block=False)


def extend(receive: SimpleQueue, send: SimpleQueue):
    def steps(state, loaded):
        yield None
        # Apply random variation to outpaint to reduce recurring patterns.
        left = utility.nudge(state["left"], 0.25)
        top = utility.nudge(state["top"], 0.25)
        right = utility.nudge(state["right"], 0.25)
        bottom = utility.nudge(state["bottom"], 0.25)
        zoom = utility.nudge(state["zoom"], 0.25)
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
                seed=utility.seed(),
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
        for state, _, (scaled, zoomed) in utility.work(receive, steps):
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
            seed=utility.seed(),
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
        for state, (scaled, _), (decoded,) in utility.work(receive, steps):
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
    def steps(_, scaled, decoded):
        yield None
        (batched,) = batcher.batch(scaled, decoded)
        yield None
        (interpolated,) = interpolator.vfi(
            ckpt_name="rife49.pth",
            frames=batched,
            clear_cache_after_n_frames=1000,
            multiplier=6,
            fast_mode=True,
            ensemble=True,
            scale_factor=0.25,
        )
        yield None
        (images,) = interpolator.vfi(
            ckpt_name="rife49.pth",
            frames=interpolated,
            clear_cache_after_n_frames=1000,
            multiplier=12,
            fast_mode=True,
            ensemble=True,
            scale_factor=1,
        )
        yield (images,)

    with torch.inference_mode():
        batcher = ImageBatch()
        interpolator = NODE_CLASS_MAPPINGS["RIFE VFI"]()
        for state, _, (images,) in utility.work(receive, steps):
            send.put((state, images[1:]), block=False)


def write(receive: SimpleQueue):
    def steps(_, images):
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
        offset, size, generation = utility.write(memory, data)
        yield None
        torch.cuda.empty_cache()
        yield (width, height, count, offset, size, generation)

    with utility.memory("image", mmap.ACCESS_WRITE) as memory, torch.inference_mode():
        for state, _, outputs in utility.work(receive, steps):
            (width, height, count, offset, size, generation) = outputs
            utility.output(
                f'{state["version"]},{state["tags"]},{width},{height},{count},{offset},{size},{generation}'
            )


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
