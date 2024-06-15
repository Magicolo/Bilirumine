import sys, threading, torch, utility, base64, json
from utility import Memory
from queue import SimpleQueue

sys.path.append("/comfy")
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


def load(state: dict):
    if state["data"]:
        data = base64.b64decode(state["data"])
        loaded = torch.frombuffer(bytearray(data), dtype=torch.uint8)
        loaded = loaded.to(dtype=torch.float32)
        loaded = loaded / 255.0
        loaded = loaded.reshape(1, *state["shape"], 3)
        return loaded
    elif state["load"]:
        (loaded, _) = LoadImage().load_image(state["load"])
    elif state["empty"]:
        (loaded,) = EmptyImage().generate(state["width"], state["height"], 1, 0)
    else:
        loaded = None
    return loaded


def read(send: SimpleQueue):
    with torch.inference_mode():
        (model, clip, vae) = CheckpointLoaderSimple().load_checkpoint(
            "dreamshaperXL_v21TurboDPMSDE.safetensors"
        )
        while True:
            state = {**utility.input(), "model": model, "clip": clip, "vae": vae}
            utility.update(state["cancel"], state["pause"], state["resume"])
            loaded = load(state)
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


def detail(receive: SimpleQueue, send: SimpleQueue, loop: SimpleQueue):

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
            send.put((state, scaled, decoded), block=False)
            if state["next"]:
                loop.put(({**state, **state["next"]}, decoded), block=False)
            elif state["loop"]:
                loop.put((state, decoded), block=False)


def interpolate(receive: SimpleQueue, send: SimpleQueue):

    def steps(state, scaled, decoded):
        yield None
        (batched,) = batcher.batch(scaled, decoded)
        yield None
        images = batched
        for scale, multiplier in state["interpolations"]:
            yield None
            (images,) = interpolator.vfi(
                ckpt_name="rife49.pth",
                frames=images,
                clear_cache_after_n_frames=5000,
                multiplier=multiplier,
                fast_mode=True,
                ensemble=True,
                scale_factor=scale,
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
        offset, size, generation = MEMORY.write(data)
        yield (width, height, count, offset, size, generation)

    with torch.inference_mode():
        for state, _, outputs in utility.work(receive, steps):
            (width, height, count, offset, size, generation) = outputs
            response = json.dumps(
                {
                    "version": state["version"],
                    "tags": state["tags"],
                    "loop": state["loop"],
                    "description": state["description"],
                    "width": width,
                    "height": height,
                    "count": count,
                    "offset": offset,
                    "size": size,
                    "generation": generation,
                }
            )
            utility.output(response)


with Memory("image") as MEMORY:
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
