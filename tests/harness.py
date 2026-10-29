"""Shared pieces for the two-copy tests: start private copies with the plugin's command line, connect their bridges,
poll a bridge expression until it holds, and keep a PASS/FAIL report."""
import json
import os
import subprocess
import sys
import time


def log(msg):
    print(time.strftime("%H:%M:%S"), msg, flush=True)


class Run:
    def __init__(self, args):
        self.args = args
        sys.path.insert(0, os.path.join(args.coop_anything, "tools", "bridge"))
        import bridge_client  # noqa: E402
        self.bc = bridge_client
        self.procs = []
        self.bridges = []
        self.failures = []
        self.report = {"steps": []}

    def check(self, name, cond, detail=""):
        self.report["steps"].append({"name": name, "ok": bool(cond), "detail": detail})
        log(("PASS " if cond else "FAIL ") + name + ("  " + detail if detail else ""))
        if not cond:
            self.failures.append(name)
        return bool(cond)

    def start(self, slot, extra):
        inst = self.args.instances[slot]
        endpoint = os.path.join(inst, "bridge-endpoint.json")
        if os.path.exists(endpoint):
            os.remove(endpoint)
        cmd = [os.path.join(inst, "superflight.exe"), "-screen-fullscreen", "0", "-screen-width", str(self.args.width),
               "-screen-height", str(self.args.height), "--coop-bridge", "0", "--coop-endpoint-file", endpoint,
               "--coop-name", "Bot%d" % slot] + extra
        log("start %d: %s" % (slot, " ".join(cmd[1:])))
        proc = subprocess.Popen(cmd, cwd=inst)
        self.procs.append(proc)
        return proc, endpoint

    def connect(self, slot, proc, endpoint_file):
        deadline = time.time() + self.args.boot_s
        endpoint = None
        while time.time() < deadline and endpoint is None and proc.poll() is None:
            if os.path.exists(endpoint_file):
                try:
                    endpoint = self.bc.endpoint_from_file(endpoint_file)
                except Exception:
                    endpoint = None
            time.sleep(0.5)
        ok = self.check("instance %d started" % slot, proc.poll() is None and endpoint is not None,
                        "exit code %s" % proc.returncode if proc.poll() is not None else endpoint or "no endpoint file")
        if not ok:
            raise SystemExit(1)
        b = self.bc.Bridge.connect(endpoint, wait_s=self.args.boot_s, timeout_s=10)
        self.bridges.append(b)
        v = b.version()
        self.report["version%d" % slot] = v
        log("instance %d bridge: %s" % (slot, v))
        return b

    def boot_pair(self, host_extra, client_extra):
        p0, e0 = self.start(0, host_extra)
        time.sleep(3)
        p1, e1 = self.start(1, client_extra)
        h = self.connect(0, p0, e0)
        c = self.connect(1, p1, e1)
        return h, c

    def ev(self, b, expr):
        return b.call("eval", {"expr": expr})["value"]

    def ex(self, b, cmd):
        r = b.call("exec", {"cmd": cmd})
        return r.get("ok"), r.get("output")

    def poll(self, b, expr, op, value, timeout_s, what):
        deadline = time.time() + timeout_s
        last = None
        while time.time() < deadline:
            try:
                r = b.call("waitFor", {"expr": expr, "op": op, "value": value, "timeoutMs": 500})
                last = r.get("value")
                if r.get("satisfied"):
                    self.check(what, True, "value %r" % (last,))
                    return True, last
            except self.bc.BridgeError as e:
                last = "error: %s" % e
            time.sleep(0.25)
        self.check(what, False, "last value %r" % (last,))
        return False, last

    def screenshot(self, slot, name):
        path = os.path.join(os.path.dirname(os.path.abspath(__file__)), name)
        if os.path.exists(path):
            os.remove(path)
        self.bridges[slot].call("screenshot", {"path": path})
        return path

    def finish(self, report_name):
        for b in self.bridges:
            try:
                b.close()
            except Exception:
                pass
        if not self.args.keep:
            for p in self.procs:
                if p.poll() is None:
                    p.kill()
        out = os.path.join(os.path.dirname(os.path.abspath(__file__)), report_name)
        with open(out, "w") as f:
            json.dump(self.report, f, indent=1, default=str)
        log("report: " + out)
        print("RESULT: " + ("PASS" if not self.failures else "FAIL " + ", ".join(self.failures)), flush=True)
        return 0 if not self.failures else 1


def common_args(ap):
    ap.add_argument("--instances", nargs=2, required=True)
    ap.add_argument("--coop-anything", required=True, help="clone of cranebeaker/coop-anything (for tools/bridge)")
    ap.add_argument("--port", type=int, default=4444)
    ap.add_argument("--width", type=int, default=800)
    ap.add_argument("--height", type=int, default=450)
    ap.add_argument("--boot-s", type=float, default=90)
    ap.add_argument("--keep", action="store_true", help="leave the games running at the end")
    return ap
