import sys, threading, mmap, utility, torch
from queue import SimpleQueue

sys.path.append("/audiocraft")
from audiocraft.models import MusicGen, MAGNeT


def load(state: dict, memory: mmap.mmap = None):
    if memory and state["size"] and state["generation"]:
        data = utility.read(memory, state["offset"], state["size"], state["generation"])
        loaded = torch.frombuffer(data, dtype=torch.float32)
        loaded = loaded.reshape(1, 1, len(loaded))
    else:
        loaded = None
    return loaded


def read(send: SimpleQueue):
    while True:
        state = utility.input()
        utility.update(state["cancel"], state["pause"], state["resume"])
        loaded = load(state)
        if loaded is None:
            continue
        send.put((state, loaded), block=False)


def process(receive: SimpleQueue, send: SimpleQueue):
    def steps(state, loaded):
        yield None
        generated = model.generate(state["prompts"]) if loaded is None else loaded
        yield None
        continued = model.generate_continuation(
            generated[:, :, -trim:], model.sample_rate, state["prompts"]
        )
        yield continued

    duration = 10
    chunk = 100
    # # model = MAGNeT.get_pretrained("facebook/magnet-small-10secs")
    # # model = MAGNeT.get_pretrained("facebook/audio-magnet-small")
    model = MusicGen.get_pretrained("facebook/musicgen-small")
    rate = model.sample_rate
    trim = duration * rate // chunk
    # # prompt = "Swirling saffron galaxies collide with molten gold rivers, as neon daisies defy gravity in a sun-drenched void of radiant yellow echoes and forgotten time's whispers."
    # prompt = "In a paradoxical forest where shades of emerald glow, vines sculpt geometric monoliths under bioluminescent canopies. Chromatic leaves dance in hypnotic synchrony, casting surreal holographic silhouettes that defy traditional arboreal form while serenading an ethereal ballad of viridescence."
    # # prompt = "A crimson vortex devours a neon-lit cityscape, its inhabitants frozen in hues of passionate scarlet, as ethereal dancers twirl within their own dreaming flames, defying time and space in an abstract symphony of chaotic beauty."
    # audio = AudioSegment.silent(duration=fade)
    # old = model.generate([prompt])

    # for _ in range(1000):
    #     now = time.time()
    #     new = model.generate_continuation(
    #         old[:, :, -trim:], model.sample_rate, [prompt]
    #     )
    #     old = new
    #     new = new.squeeze().cpu().numpy()
    #     new = (new * 32767).astype(numpy.int16)

    #     duration = audio.duration_seconds
    #     segment = AudioSegment(
    #         new.tobytes(), frame_rate=rate, sample_width=2, channels=1
    #     )
    #     audio = audio.append(segment, crossfade=fade)
    #     audio.export("/input/test.wav", format="wav")
    #     elapsed = time.time() - now
    #     print(
    #         f"Done after '{elapsed}' with speed '{(audio.duration_seconds - duration) / elapsed}'."
    #     )

    for state, _, audio in utility.work(receive, steps):
        send.put((state, audio), block=False)


def write(receive: SimpleQueue):
    def steps(state, audio):
        yield True

    with utility.memory("audiocraft", mmap.ACCESS_WRITE) as memory:
        for state, _, outputs in utility.work(receive, steps):
            (width, height, count, offset, size, generation) = outputs
            utility.output(
                f'{state["version"]},{state["tags"]},{width},{height},{count},{offset},{size},{generation}'
            )


a = SimpleQueue()
b = SimpleQueue()
threads = [
    threading.Thread(target=read, args=(a,)),
    threading.Thread(target=process, args=(a, b)),
    threading.Thread(target=write, args=(b,)),
]
for thread in threads:
    thread.start()
for thread in threads:
    thread.join()
