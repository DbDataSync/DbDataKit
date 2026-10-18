#!/usr/bin/env python3
"""
Drives the real TUI in a pseudo-terminal and prints the screen after each step, rendered by pyte (a terminal emulator). Terminal.Gui has no headless driver in
its package, so this is how the screens are looked at without a person: start the app, send keys, read the screen.

  pip install pyte            (or put pyte and wcwidth on PYTHONPATH)
  python3 scripts/tui_drive.py <project> '[["label", "keys"], ...]'

Keys are sent as written: "\\r" is Enter, "\\t" Tab, "\\x1b" Esc, "\\x1b[B" Down, "\\x1bOP" F1, "\\x11" Ctrl+Q. The app is `dotnet src/DbDataBuild.Cli/bin/Debug/net10.0/dbdatabuild.dll tui`
unless TUI_EXE names a published executable. SEGROWS=2,3 prints the colour runs of those screen rows (to see where a pane's text is invisible).
Logins come from the environment, as for the CLI.
"""
import fcntl, json, os, pty, select, signal, struct, sys, termios, time

import pyte

ROWS, COLS = int(os.environ.get("TUI_ROWS", 40)), int(os.environ.get("TUI_COLS", 130))
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def run(project, steps, wait=1.2, start_wait=4):
    pid, fd = pty.fork()
    if pid == 0:
        env = dict(os.environ, TERM="xterm-256color")
        os.chdir(REPO)
        exe = os.environ.get("TUI_EXE")
        if exe:
            os.execvpe(exe, [exe, "ui", "terminal", "--project", project], env)
        os.execvpe("dotnet", ["dotnet", "src/DbDataBuild.Cli/bin/Debug/net10.0/dbdatabuild.dll", "ui", "terminal", "--project", project], env)
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
    screen = pyte.Screen(COLS, ROWS)
    stream = pyte.ByteStream(screen)

    def pump(seconds):
        end = time.time() + seconds
        while time.time() < end:
            ready, _, _ = select.select([fd], [], [], 0.1)
            if ready:
                try:
                    data = os.read(fd, 65536)
                except OSError:
                    return False
                if not data:
                    return False
                stream.feed(data)
        return True

    def runs(row):
        out, cur, start = [], None, 0
        for x in range(COLS):
            ch = screen.buffer[row][x]
            key = (ch.bg, ch.fg, ch.reverse)
            if key != cur:
                if cur is not None:
                    out.append((start, x - 1, cur))
                cur, start = key, x
        out.append((start, COLS - 1, cur))
        return out

    def show(label):
        print(f"----- {label} " + "-" * 60)
        for r in filter(None, os.environ.get("SEGROWS", "").split(",")):
            print("row", r, runs(int(r)))
        for line in screen.display:
            print(line.rstrip())

    pump(start_wait)
    show("start")
    for label, keys in steps:
        os.write(fd, keys.encode())
        alive = pump(wait)
        show(label)
        if not alive:
            print("(process exited)")
            break
    try:
        os.kill(pid, signal.SIGKILL)
    except OSError:
        pass


if __name__ == "__main__":
    run(sys.argv[1], json.loads(sys.argv[2]) if len(sys.argv) > 2 else [])
