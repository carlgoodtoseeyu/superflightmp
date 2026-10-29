"""Steam lobby checks. One private copy, driven through the bridge; the copy signs in through the Steam client that
is running on this PC, so this touches that Steam account (a friends-only lobby exists while the test runs).

Host mode (default): Steam is up, a lobby gets created, the session is hosting over the Steam transport, the lobby
has one member, Leave tears the lobby down. Prints RESULT: PASS/FAIL.

    python -I tests/run_steam.py --instances <copy0> <copy1> --coop-anything <clone>

Two-PC mode: on PC A run host mode with --keep --hold 300 and read the lobby code it prints; on PC B (another Steam
account that owns the game) run with --join CODE: it joins the lobby, gets the host's seed, and sees state flowing.

    python -I tests/run_steam.py --instances <copy0> <copy1> --coop-anything <clone> --join CODE
"""
import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from harness import Run, common_args, log  # noqa: E402


def main():
    ap = common_args(argparse.ArgumentParser())
    ap.add_argument("--join", default="", help="lobby code from a host running on another PC")
    ap.add_argument("--hold", type=float, default=20, help="seconds to keep hosting before leaving")
    args = ap.parse_args()
    run = Run(args)
    try:
        p, e = run.start(0, ["--coop-autoflight", "--coop-bot"])
        b = run.connect(0, p, e)
        run.poll(b, "steamReady", "truthy", 0, 30, "Steam API initialised in the game")
        run.poll(b, "inFlight", "truthy", 0, 60, "in flight")

        if args.join:
            ok, out = run.ex(b, "steamjoin " + args.join)
            run.check("steamjoin accepted", ok, out)
            run.poll(b, "joined", "truthy", 0, 60, "joined the host through Steam")
            run.check("transport is steam", run.ev(b, "transport") == "steam", run.ev(b, "transport"))
            run.poll(b, "seedsMatch", "truthy", 0, 30, "in the host's world")
            run.poll(b, "playerCount", ">=", 2, 10, "host counted as a player")
            run.poll(b, "statesApplied", ">", 20, 20, "host state arriving")
            run.poll(b, "renderedPuppets", ">=", 1, 20, "host puppet rendered")
            time.sleep(args.hold)
            run.check("still joined after hold", run.ev(b, "joined"), "lastError %r" % run.ev(b, "lastError"))
            run.screenshot(0, "shot-steam-client.png")
        else:
            ok, out = run.ex(b, "steamhost")
            run.check("steamhost accepted", ok, out)
            run.poll(b, "inLobby", "truthy", 0, 30, "lobby created")
            code = run.ev(b, "lobbyId")
            log("LOBBY CODE: %s" % code)
            run.report["lobby"] = code
            run.poll(b, "connected", "truthy", 0, 10, "session is up")
            run.check("session is hosting", run.ev(b, "role") == "host", run.ev(b, "role"))
            run.check("transport is steam", run.ev(b, "transport") == "steam", run.ev(b, "transport"))
            run.poll(b, "lobbyMembers", "==", 1, 10, "one lobby member (us)")
            run.check("no error", run.ev(b, "lastError") == "", run.ev(b, "lastError"))
            run.screenshot(0, "shot-steam-host.png")
            log("holding the lobby for %.0f s" % args.hold)
            deadline = time.time() + args.hold
            while time.time() < deadline:
                time.sleep(5)
                n = run.ev(b, "playerCount")
                if n >= 2:
                    log("a player joined: %s players" % n)
                    run.poll(b, "statesApplied", ">", 20, 20, "client state arriving")
                    run.report["joined_player"] = True
                    break
            run.ex(b, "leave")
            run.poll(b, "inLobby", "==", 0, 10, "lobby left")
            run.poll(b, "connected", "==", 0, 10, "session closed")
            run.check("role is none", run.ev(b, "role") == "none", run.ev(b, "role"))
        run.check("instance still running", p.poll() is None, "exit code %s" % p.returncode)
    except SystemExit:
        pass
    except Exception as e:  # noqa: BLE001
        run.check("harness", False, "%s: %s" % (type(e).__name__, e))
    return run.finish("last-steam-run.json")


if __name__ == "__main__":
    sys.exit(main())
