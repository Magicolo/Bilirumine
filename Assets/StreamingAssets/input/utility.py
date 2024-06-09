import sys, threading, ast, random, mmap
from queue import SimpleQueue, Empty
from typing import Optional, Tuple


def seed():
    return random.randint(1, 2**64)


def nudge(value, by):
    return int(value * (random.random() * by + 1.0))


def input():
    with INPUT_LOCK:
        line = sys.stdin.readline()
    return ast.literal_eval(line)


def output(message):
    line = f"{message}\n"
    with OUTPUT_LOCK:
        sys.stdout.write(line)
        sys.stdout.flush()


def error(message):
    line = f"{message}\n"
    with ERROR_LOCK:
        sys.stderr.write(line)
        sys.stderr.flush()


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


def update(cancel, pause, resume):
    global CANCEL, PAUSE
    if cancel:
        CANCEL = CANCEL.union(cancel)
    if pause:
        PAUSE = PAUSE.union(pause)
    if cancel or resume:
        PAUSE = PAUSE.difference(cancel + resume)


def align(pointer: int) -> int:
    global ALIGN
    remain = pointer % ALIGN
    if remain == 0:
        return pointer
    else:
        return pointer + ALIGN - remain


def memory(name: str, access: int):
    global CAPACITY
    with open(f"/dev/shm/bilirumine_{name}", "r+b") as file:
        return mmap.mmap(file.fileno(), CAPACITY, access=access)


def read(memory: mmap.mmap, offset: int, size: int, generation: int) -> Optional[bytes]:
    global NEXT, CAPACITY, GENERATION, MEMORY_LOCK

    if size <= 0 or offset < 0 or offset + size > CAPACITY:
        return None

    for _ in range(5):
        with MEMORY_LOCK:
            age = GENERATION - generation
            if age < 0:
                GENERATION = generation
            elif age > 1:
                return None
            elif age == 1 and NEXT > offset:
                return None

            try:
                memory.seek(offset)
                return memory.read(size)
            except Exception as exception:
                error(f"Failed to read memory at '{offset} : {size}': {exception}")


def write(memory: mmap.mmap, bytes: bytes) -> Tuple[int, int, int]:
    global NEXT, CAPACITY, GENERATION, PAD, MEMORY_LOCK

    size = len(bytes)
    if size <= 0:
        return (0, 0, 0)

    for _ in range(5):
        with MEMORY_LOCK:
            add = align(PAD + size)
            if NEXT + add > CAPACITY:
                GENERATION, NEXT = GENERATION + 1, 0
            generation, offset, NEXT = GENERATION, NEXT, NEXT + add

            try:
                memory.seek(offset)
                return offset, memory.write(bytes), generation
            except Exception as exception:
                error(f"Failed to write memory at '{offset} : {size}': {exception}")
    return (0, 0, 0)

WAIT = 0.1
ALIGN = 8
PAD = 1024
PAUSE = set()
CANCEL = set()
GENERATION = 1
CAPACITY = 2**31 - 1
NEXT = PAD
MEMORY_LOCK = threading.Lock()
INPUT_LOCK = threading.Lock()
OUTPUT_LOCK = threading.Lock()
ERROR_LOCK = threading.Lock()
