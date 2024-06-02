import sys, threading, mmap, utility, torch
from typing import Optional
from queue import SimpleQueue

sys.path.append("/audiocraft")
from audiocraft.models import MusicGen, MAGNeT


def load(state: dict, memory: Optional[mmap.mmap] = None):
    if memory and state["size"] and state["generation"]:
        data = utility.read(memory, state["offset"], state["size"], state["generation"])
        loaded = torch.frombuffer(data, dtype=torch.float32)
        loaded = loaded.reshape(1, 1, len(loaded))
    else:
        loaded = None


def read(send: SimpleQueue):
    with utility.memory("audio", mmap.ACCESS_READ) as memory:
        while True:
            state = utility.input()
            utility.update(state["cancel"], state["pause"], state["resume"])
            loaded = load(state, memory)
            if loaded is None:
                continue
            send.put((state, loaded), block=False)


def process(receive: SimpleQueue, send: SimpleQueue):
    def steps(state, loaded):
        yield None
        if loaded is None:
            clips = model.generate(state["prompts"])
        else:
            clips = model.generate_continuation(
                loaded[:, :, -trim:], rate, state["prompts"]
            )
        yield (clips,)

    duration = 10
    chunk = 100
    # # model = MAGNeT.get_pretrained("facebook/magnet-small-10secs")
    # # model = MAGNeT.get_pretrained("facebook/audio-magnet-small")
    model = MusicGen.get_pretrained("facebook/musicgen-small")
    rate = model.sample_rate
    trim = duration * rate // chunk

    for state, _, (clips,) in utility.work(receive, steps):
        state = {**state, "rate": rate}
        send.put((state, clips), block=False)
        if state["loop"]:
            receive.put((state, clips), block=False)


def write(receive: SimpleQueue):
    def steps(_, clips):
        yield None
        [count, channels, samples] = clips.shape
        data = clips.numpy().tobytes()
        yield None
        offset, size, generation = utility.write(memory, data)
        yield None
        torch.cuda.empty_cache()
        yield (samples, channels, count, offset, size, generation)

    with utility.memory("audio", mmap.ACCESS_WRITE) as memory:
        for state, _, outputs in utility.work(receive, steps):
            (samples, channels, count, offset, size, generation) = outputs
            utility.output(
                f'{state["version"]},{state["rate"]},{samples},{channels},{count},{offset},{size},{generation}'
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
