"""Level changes in a session: the host's portal choice, deferred follow for the client, the client's crash and
portal choices, and the scoreboard rows. Two private copies, driven through the bridge. Prints RESULT: PASS/FAIL.

    python -I tests/run_portal.py --instances <copy0> <copy1> --coop-anything <clone>
"""
import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from harness import Run, common_args, log  # noqa: E402


def main():
    args = common_args(argparse.ArgumentParser()).parse_args()
    run = Run(args)
    try:
        h, c = run.boot_pair(["--coop-host", str(args.port), "--coop-autoflight", "--coop-bot"],
                             ["--coop-join", "127.0.0.1:%d" % args.port, "--coop-autoflight", "--coop-bot"])
        run.poll(c, "joined", "truthy", 0, 60, "client joined")
        run.poll(h, "inFlight", "truthy", 0, 60, "host in flight")
        run.poll(c, "inFlight", "truthy", 0, 60, "client in flight")
        run.poll(c, "seedsMatch", "truthy", 0, 30, "client in the host's world")
        seed0 = run.ev(h, "gameSeed")

        # 1. host portal -> choice screen, game paused, nothing sent yet
        run.ex(h, "portal")
        run.poll(h, "choiceOpen", "truthy", 0, 10, "host sees the choice after a portal")
        run.check("host choice reason is portal", run.ev(h, "choiceReason") == "Portal", run.ev(h, "choiceReason"))
        run.check("host game paused while choosing", float(run.ev(h, "timeScale")) == 0.0, "timeScale %s" % run.ev(h, "timeScale"))
        run.check("host seed unchanged while choosing", run.ev(h, "gameSeed") == seed0)
        run.check("client has no pending seed yet", run.ev(c, "pendingSeed") == 0 and not run.ev(c, "choiceOpen"))
        rows = run.ev(h, "scoreboardRows")
        run.check("scoreboard lists both players", rows == 2, "rows %s" % rows)
        run.screenshot(0, "shot-host-choice.png")
        time.sleep(1.5)

        # 2. host respawns in the same level
        run.ex(h, "choose respawn")
        run.poll(h, "choiceOpen", "==", 0, 10, "host choice closed after respawn")
        run.check("host still in the same level", run.ev(h, "gameSeed") == seed0)
        run.check("host unpaused", float(run.ev(h, "timeScale")) == 1.0)
        run.poll(h, "inFlight", "truthy", 0, 10, "host flying again")

        # 3. host proceeds -> new seed; client only gets it as pending
        run.ex(h, "portal")
        run.poll(h, "choiceOpen", "truthy", 0, 10, "host choice open again")
        run.ex(h, "choose proceed")
        run.poll(h, "choiceOpen", "==", 0, 10, "host choice closed after proceed")
        seed1 = None
        for _ in range(40):
            seed1 = run.ev(h, "gameSeed")
            if seed1 != seed0:
                break
            time.sleep(0.25)
        run.check("host is in a new level", seed1 != seed0 and seed1 != 0, "%s -> %s" % (seed0, seed1))
        run.poll(c, "pendingSeed", "==", seed1, 15, "client received the host's new seed as pending")
        run.check("client was not pulled out of its level", run.ev(c, "gameSeed") == seed0, "client seed %s" % run.ev(c, "gameSeed"))
        run.check("both still see 2 players", run.ev(h, "playerCount") == 2 and run.ev(c, "playerCount") == 2)

        # 4. client crashes -> choice with pending; respawn keeps it in the old level
        run.ex(c, "choose respawn")  # in case a bot crash already opened it
        run.ex(c, "die")
        run.poll(c, "choiceOpen", "truthy", 0, 15, "client sees the choice after a crash")
        run.check("client choice reason is crash", run.ev(c, "choiceReason") == "Crash", run.ev(c, "choiceReason"))
        run.check("client game not paused after a crash", float(run.ev(c, "timeScale")) == 1.0)
        run.screenshot(1, "shot-client-choice.png")
        time.sleep(1.5)
        run.ex(c, "choose respawn")
        run.poll(c, "choiceOpen", "==", 0, 10, "client choice closed after respawn")
        run.check("client stayed in its level", run.ev(c, "gameSeed") == seed0)
        run.check("client still has the pending seed", run.ev(c, "pendingSeed") == seed1)
        run.poll(c, "inFlight", "truthy", 0, 10, "client flying again")

        # 5. client portal -> choice; proceed joins the host's level
        run.ex(c, "portal")
        run.poll(c, "choiceOpen", "truthy", 0, 10, "client sees the choice after its portal")
        run.ex(c, "choose proceed")
        run.poll(c, "choiceOpen", "==", 0, 10, "client choice closed after proceed")
        run.poll(c, "gameSeed", "==", seed1, 10, "client is now in the host's level")
        run.check("client pending cleared", run.ev(c, "pendingSeed") == 0)
        run.poll(c, "inFlight", "truthy", 0, 10, "client flying in the new level")
        run.poll(c, "seedsMatch", "truthy", 0, 10, "client session seed matches")

        # 6. client portal with nothing pending: respawn only, no jump ahead of the host
        run.ex(c, "portal")
        run.poll(c, "choiceOpen", "truthy", 0, 10, "client choice opens with no pending seed")
        run.ex(c, "choose proceed")  # must behave like respawn: nothing to proceed to
        run.poll(c, "choiceOpen", "==", 0, 10, "client choice closed")
        run.check("client cannot get ahead of the host", run.ev(c, "gameSeed") == seed1)

        for i, p in enumerate(run.procs):
            run.check("instance %d still running" % i, p.poll() is None, "exit code %s" % p.returncode)
    except SystemExit:
        pass
    except Exception as e:  # noqa: BLE001
        run.check("harness", False, "%s: %s" % (type(e).__name__, e))
    return run.finish("last-portal-run.json")


if __name__ == "__main__":
    sys.exit(main())
