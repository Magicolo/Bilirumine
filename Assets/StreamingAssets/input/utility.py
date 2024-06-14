import sys, threading, ast, random, mmap, os, time
from queue import SimpleQueue, Empty
from typing import Optional, Tuple


class Memory:

    def __init__(self, name: str, align: int = 8, capacity: int = 2**31 - 1):
        self.capacity = capacity
        self.lock = f"/input/{name}.lock"
        self.next = 0
        self.align = align
        self.generation = 1
        with open(f"/dev/shm/bilirumine_{name}", "r+b") as file:
            self.memory = mmap.mmap(file.fileno(), capacity, access=mmap.ACCESS_WRITE)
        self._release()

    def read(self, offset: int, size: int, generation: int) -> Optional[bytes]:
        if size <= 0 or offset < 0 or offset + size > self.capacity:
            return None

        self._acquire()
        try:
            age = self.generation - generation
            if age < 0:
                self.generation = generation
            elif age > 1:
                return None
            elif age == 1 and self.next > offset:
                return None
            return self.memory[offset : offset + size]
        except Exception as exception:
            error(f"Failed to read memory at '{offset} : {size}': {exception}")
        finally:
            self._release()

    def write(self, data: bytes) -> Tuple[int, int, int]:
        size = len(data)
        if size <= 0:
            return (0, 0, 0)

        self._acquire()
        try:
            if self.next + size > self.capacity:
                self.generation, self.next = self.generation + 1, 0
            generation, offset = self.generation, self.next
            self.next = align(self.next + size, self.align)
            self.memory[offset : offset + size] = data
            return offset, size, generation
        except Exception as exception:
            error(f"Failed to write memory at '{offset} : {size}': {exception}")
            return (0, 0, 0)
        finally:
            self._release()

    def _acquire(self):
        while True:
            for _ in range(10):
                for _ in range(10):
                    try:
                        return os.open(self.lock, os.O_CREAT | os.O_EXCL | os.O_RDWR)
                    except FileExistsError:
                        pass
                time.sleep(0)
            time.sleep(0.001)

    def _release(self):
        try:
            os.remove(self.lock)
        except FileNotFoundError:
            pass


def seed():
    return random.randint(1, 2**64)


def nudge(value, by):
    return int(value * (random.random() * by + 1.0))


def input():
    with INPUT:
        line = sys.stdin.readline()
    return ast.literal_eval(line)


def output(message):
    line = f"{message}\n"
    with OUTPUT:
        sys.stdout.write(line)
        sys.stdout.flush()


def error(message):
    line = f"{message}\n"
    with ERROR:
        sys.stderr.write(line)
        sys.stderr.flush()


def align(pointer: int, align: int) -> int:
    if pointer % align == 0:
        return pointer
    else:
        return pointer + align - pointer % align


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

WAIT = 0.1
PAUSE = set()
CANCEL = set()
INPUT = threading.Lock()
OUTPUT = threading.Lock()
ERROR = threading.Lock()
