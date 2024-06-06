import sys, threading, mmap, utility, torch
from typing import Optional
from queue import SimpleQueue

sys.path.append("/audiocraft")
from audiocraft.models import MusicGen


def load(state: dict, memory: Optional[mmap.mmap] = None):
    if memory and state["size"] and state["generation"]:
        data = utility.read(memory, state["offset"], state["size"], state["generation"])
        if data is not None:
            loaded = torch.frombuffer(data, dtype=torch.float32)
            loaded = loaded.reshape(1, 1, len(loaded))
            return loaded


def read(send: SimpleQueue):
    with utility.memory("sound", mmap.ACCESS_READ) as memory:
        while True:
            state = utility.input()
            utility.update(state["cancel"], state["pause"], state["resume"])
            loaded = load(state, memory)
            if loaded is None and not state["empty"]:
                continue
            send.put((state, loaded), block=False)


def process(receive: SimpleQueue, send: SimpleQueue):
    def steps(state, loaded):
        yield None
        model.set_generation_params(duration=state["duration"])
        if loaded is None:
            clips = model.generate(state["prompts"])
        else:
            trim = int(state["duration"] * state["overlap"] * RATE)
            clips = model.generate_continuation(
                loaded[:, :, -trim:], RATE, state["prompts"]
            )
        yield (clips,)

    model = MusicGen.get_pretrained("facebook/musicgen-small")

    for state, _, (clips,) in utility.work(receive, steps):
        send.put((state, clips), block=False)
        if state["loop"]:
            receive.put((state, clips), block=False)


def write(receive: SimpleQueue):
    def steps(_, clips):
        yield None
        [count, channels, samples] = clips.shape
        data = clips.cpu().numpy().tobytes()
        yield None
        offset, size, generation = utility.write(memory, data)
        yield None
        torch.cuda.empty_cache()
        yield (samples, channels, count, offset, size, generation)

    with utility.memory("sound", mmap.ACCESS_WRITE) as memory:
        for state, _, outputs in utility.work(receive, steps):
            (samples, channels, count, offset, size, generation) = outputs
            utility.output(
                f'{state["version"]},{state["tags"]},{state["overlap"]},{RATE},{samples},{channels},{count},{offset},{size},{generation}'
            )


RATE = 32000
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