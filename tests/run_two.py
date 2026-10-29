"""Two real Superflight copies on one PC: instance 0 hosts, instance 1 joins over UDP. Both enter flight.
Asserts through the in-game bridge that each side sees the other, both are in the host's world, state keeps
arriving and the puppets are drawn. Prints RESULT: PASS or RESULT: FAIL and exits 0 or 1.

    python -I tests/run_two.py --instances C:\\path\\inst0 C:\\path\\inst1 --coop-anything <clone of coop-anything>

Needs the bridge client from coop-anything (tools/bridge/bridge_client.py). Nothing here touches the Steam install:
the instance folders are the private copies made by the workspace setup.
"""
import argparse
import json
import os
import subprocess
import sys
import time


def log(msg):
    print(time.strftime("%H:%M:%S"), msg, flush=True)


def start(inst, slot, args, extra):
    exe = os.path.join(inst, "superflight.exe")
    endpoint = os.path.join(inst, "bridge-endpoint.json")
    if os.path.exists(endpoint):
        os.remove(endpoint)
    cmd = [exe, "-screen-fullscreen", "0", "-screen-width", str(args.width), "-screen-height", str(args.height),
           "--coop-bridge", "0", "--coop-endpoint-file", endpoint, "--coop-name", "Bot%d" % slot] + extra
    log("start %d: %s" % (slot, " ".join(cmd[1:])))
    proc = subprocess.Popen(cmd, cwd=inst)
    return proc, endpoint


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--instances", nargs=2, required=True)
    ap.add_argument("--coop-anything", required=True, help="clone of cranebeaker/coop-anything (for tools/bridge)")
    ap.add_argument("--port", type=int, default=4444)
    ap.add_argument("--width", type=int, default=800)
    ap.add_argument("--height", type=int, default=450)
    ap.add_argument("--boot-s", type=float, default=90)
    ap.add_argument("--hold-s", type=float, default=20, help="seconds of flight to watch after both joined")
    ap.add_argument("--keep", action="store_true", help="leave the games running at the end")
    args = ap.parse_args()

    sys.path.insert(0, os.path.join(args.coop_anything, "tools", "bridge"))
    from bridge_client import Bridge, endpoint_from_file, BridgeError  # noqa: E402

    procs = []
    bridges = []
    failures = []
    report = {"steps": []}

    def check(name, cond, detail=""):
        report["steps"].append({"name": name, "ok": bool(cond), "detail": detail})
        log(("PASS " if cond else "FAIL ") + name + ("  " + detail if detail else ""))
        if not cond:
            failures.append(name)

    def poll(bridge, expr, op, value, timeout_s, what):
        deadline = time.time() + timeout_s
        last = None
        while time.time() < deadline:
            try:
                r = bridge.call("waitFor", {"expr": expr, "op": op, "value": value, "timeoutMs": 500})
                last = r.get("value")
                if r.get("satisfied"):
                    return True, last
            except BridgeError as e:
                last = "error: %s" % e
            time.sleep(0.25)
        check(what, False, "last value %r" % (last,))
        return False, last

    try:
        host_extra = ["--coop-host", str(args.port), "--coop-autoflight", "--coop-bot"]
        client_extra = ["--coop-join", "127.0.0.1:%d" % args.port, "--coop-autoflight", "--coop-bot"]
        p0, e0 = start(args.instances[0], 0, args, host_extra)
        procs.append(p0)
        time.sleep(3)
        p1, e1 = start(args.instances[1], 1, args, client_extra)
        procs.append(p1)

        for i, (proc, ep) in enumerate(((p0, e0), (p1, e1))):
            deadline = time.time() + args.boot_s
            endpoint = None
            while time.time() < deadline and endpoint is None:
                if proc.poll() is not None:
                    break
                if os.path.exists(ep):
                    try:
                        endpoint = endpoint_from_file(ep)
                    except Exception:
                        endpoint = None
                time.sleep(0.5)
            check("instance %d started" % i, proc.poll() is None and endpoint is not None,
                  "exit code %s" % proc.returncode if proc.poll() is not None else endpoint or "no endpoint file")
            if endpoint is None:
                raise SystemExit(1)
            b = Bridge.connect(endpoint, wait_s=args.boot_s, timeout_s=10)
            bridges.append(b)
            v = b.version()
            report["version%d" % i] = v
            log("instance %d bridge: %s" % (i, v))

        h, c = bridges
        ok, _ = poll(h, "inFlight", "truthy", 0, 60, "host in flight")
        ok, _ = poll(c, "inFlight", "truthy", 0, 60, "client in flight")
        ok, _ = poll(c, "joined", "truthy", 0, 30, "client joined")
        ok, _ = poll(h, "playerCount", "==", 2, 30, "host sees 2 players")
        ok, _ = poll(c, "playerCount", "==", 2, 30, "client sees 2 players")
        ok, _ = poll(c, "seedsMatch", "truthy", 0, 30, "client is in the session world")
        hs = h.call("eval", {"expr": "gameSeed"})["value"]
        cs = c.call("eval", {"expr": "gameSeed"})["value"]
        check("same world seed on both", hs == cs and hs != 0, "host %s client %s" % (hs, cs))
        ok, _ = poll(h, "visiblePuppets", ">=", 1, 30, "host draws the client's puppet")
        ok, _ = poll(c, "visiblePuppets", ">=", 1, 30, "client draws the host's puppet")

        # state must keep flowing while both fly, and both must actually move (the bots steer and quick-reset)
        def pos(b):
            return tuple(float(b.call("eval", {"expr": "local.pos." + ax})["value"]) for ax in "xyz")

        def dist(p, q):
            return sum((x - y) ** 2 for x, y in zip(p, q)) ** 0.5

        a0 = h.call("eval", {"expr": "statesApplied"})["value"]
        b0 = c.call("eval", {"expr": "statesApplied"})["value"]
        hp0, cp0 = pos(h), pos(c)
        hmax = cmax = 0.0
        hrend = crend = 0
        end = time.time() + args.hold_s
        while time.time() < end:
            time.sleep(1)
            hmax = max(hmax, dist(hp0, pos(h)))
            cmax = max(cmax, dist(cp0, pos(c)))
            hrend = max(hrend, int(h.call("eval", {"expr": "player.1.rendered"})["value"]))
            crend = max(crend, int(c.call("eval", {"expr": "player.0.rendered"})["value"]))
        a1 = h.call("eval", {"expr": "statesApplied"})["value"]
        b1 = c.call("eval", {"expr": "statesApplied"})["value"]
        check("host player moved", hmax > 20, "max %.1f units from start in %ss" % (hmax, args.hold_s))
        check("client player moved", cmax > 20, "max %.1f units from start in %ss" % (cmax, args.hold_s))
        check("host drew the client's wingsuit meshes", hrend > 0, "most renderers drawn in one frame: %d" % hrend)
        check("client drew the host's wingsuit meshes", crend > 0, "most renderers drawn in one frame: %d" % crend)
        check("puppets are mesh clones, not spheres", h.call("eval", {"expr": "clonedPuppets"})["value"] >= 1 and c.call("eval", {"expr": "clonedPuppets"})["value"] >= 1)
        shots = []
        for i, b in enumerate(bridges):
            path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "shot-%d.png" % i)
            if os.path.exists(path):
                os.remove(path)
            b.call("screenshot", {"path": path})
            shots.append(path)
        time.sleep(2)
        for i, path in enumerate(shots):
            check("screenshot %d written" % i, os.path.exists(path) and os.path.getsize(path) > 1000, path)
        rate = args.hold_s * 5  # expect at least 5 updates/s of the configured 20
        check("host keeps receiving client state", a1 - a0 >= rate, "%d updates in %ss" % (a1 - a0, args.hold_s))
        check("client keeps receiving host state", b1 - b0 >= rate, "%d updates in %ss" % (b1 - b0, args.hold_s))

        # puppet follows the remote within a tolerance: the host's copy of the client position vs the client's own
        cpos = c.call("eval", {"expr": "local.pos.x"})["value"], c.call("eval", {"expr": "local.pos.y"})["value"], c.call("eval", {"expr": "local.pos.z"})["value"]
        st = h.call("state", {"sections": ["players"]})["players"]
        p1 = [p for p in st if p["id"] == 1]
        check("host has client as player 1", len(p1) == 1, json.dumps(st)[:300])
        if p1:
            hp = p1[0]["pos"]
            dist = sum((float(x) - float(y)) ** 2 for x, y in zip(hp, cpos)) ** 0.5
            check("host's copy of client position is close", dist < 200, "distance %.1f (client %s host %s)" % (dist, cpos, hp))
        for i, b in enumerate(bridges):
            report["state%d" % i] = b.call("state", {})
        for i, proc in enumerate(procs):
            check("instance %d still running" % i, proc.poll() is None, "exit code %s" % proc.returncode)
    except SystemExit:
        pass
    except Exception as e:  # noqa: BLE001
        check("harness", False, "%s: %s" % (type(e).__name__, e))
    finally:
        for b in bridges:
            try:
                b.close()
            except Exception:
                pass
        if not args.keep:
            for p in procs:
                if p.poll() is None:
                    p.kill()
        out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "last-run.json")
        with open(out, "w") as f:
            json.dump(report, f, indent=1, default=str)
        log("report: " + out)
    print("RESULT: " + ("PASS" if not failures else "FAIL " + ", ".join(failures)), flush=True)
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
