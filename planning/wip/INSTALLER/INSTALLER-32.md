# INSTALLER-32 (work in progress)

## Built

The text below is in `docs/install.md` and `docs/release-notes-pending.md` on this branch; `check_doc_inputs.py` passes.

## Open: the owner approves the text

1. "Pairing", the diagram: `keeps the token's hash` becomes `keeps a verifier made from the token`.
2. "Pairing", "Join tokens are rows of the console's state": "holding the token's hash" becomes "holding a verifier made from the token", and two
   sentences follow "who made it and when it ends.": "The verifier is the public half of a key derived from the token. It checks a join's signature
   and cannot make one, so a copy of the console's state is not enough to join a server."
3. "Logs and the startup-failure file", a new paragraph after the first:

   > Where a server keeps its logs and its failure file depends on its data folder:
   >
   > - The data folder is the service's folder of an install (`C:\ProgramData\DbDataKit\runner`, Linux `/var/lib/dbdatakit/runner`): the shared
   >   `logs` folder above (Linux `/var/log/dbdatakit`).
   > - The data folder has the service's folder name under another root (`D:\suite\runner`): a `logs` folder beside it (`D:\suite\logs`).
   > - Any other data folder, such as a run from a terminal on a folder of its own or a test's folder: a `logs` folder inside it. Such a run never
   >   writes to the machine's logs folder.
   >
   > Example: when `dbdatarunner server run --repo C:\dev\runner-dev` fails at the ports step, it writes
   > `C:\dev\runner-dev\logs\startup-failure-dbdatarunner.txt`. On Linux, `--repo /home/ana/runner-dev` writes
   > `/home/ana/runner-dev/logs/dbdatarunner/startup-failure-dbdatarunner.txt`.

4. "Service names, accounts and descriptions", before the Linux line: "Each Windows service's display name, the name `services.msc` lists, is its
   service name (`DbDataRunner`)." After the Linux line: "Its systemd unit is named as the program too (`dbdatarunner.service`), and the unit's
   `Description=` is the service's description above. Example: `systemctl status dbdatarunner` shows `dbdatarunner.service - DbDataKit runner: runs
   published tasks (data loads, replication, deployments and quality checks) on schedule or on demand.`"
5. `docs/release-notes-pending.md`, after the entry on the `DbDataGateway` service name: "**Changed: each service's systemd unit carries the same
   description as its Windows service, and each Windows service's display name is its service name (`DbDataGateway`, where an earlier Gateway showed
   `dbdatagateway`).** `systemctl status dbdatarunner` shows `DbDataKit runner: runs published tasks ...`. What to do: run `install.sh --system` of
   the new release again, which rewrites a unit that differs, and on Windows run each product's `service install` again from an elevated prompt."

When the owner approves (or changes) it: apply any change, remove the INSTALLER-32 row from `planning/INSTALLER.md`, delete this file.
