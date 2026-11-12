# Scaling out from one machine

A single-machine install keeps every service's state in SQLite on that machine. Growing is **a series of steps, each one on its own, none a reinstall**: move the state to SQL Server or PostgreSQL, put servers on other machines, add instances of a serving group. Each step can be done days after the one before.

## 1. Move the console's state to a server engine

Stop the console (the move works offline and refuses a console that is running), then on the machine:

```
dbdatakit state move --to-engine postgres --to-connection "Host=db;Database=dbdatakit;Username=dbdatakit;Password=env:DBDATAKIT_STATE_PASSWORD" --yes
dbdatakit service restart
```

(`--to-engine mssql` for SQL Server.) The move copies every table, checks the copy against a snapshot and against the old database, and only then writes the `state:` section of `settings.yaml`; the old SQLite file is left as it was, and the command prints the way back. The password stays a reference (`env:`, `secret:` or `file:`); a written-out password is refused.

What moves is everything the console keeps in its state: people and sessions, the setup checklist, delivery messages, the results of data quality checks and which of them were alerted (a failure alerted before the move is not alerted again after it), and the audit. What does **not** move is files, because they were never state: the servers the console is paired with, the console's certificate and secrets, `settings.yaml`, `bundle.json` and the bundle token. They stay where they are, and the console on the new engine still pairs with the servers of this machine by itself (see *First run*, the bundle).

Do the same for each server of the machine (`dbdatasync state move`, ...) when it should share a database; `state show` and `state test` say where each one is.

## 2. Put a server on another machine

Install the product there and pair it as any server is paired: it announces itself to the console (`--announce-url`), and an administrator confirms it (the Servers page, or `dbdatakit instance pending` and `dbdatakit instance confirm`). A server on another machine has no bundle token and is never paired by itself: that is the line the bundle draws between *this machine* and *another one*.

## 3. Add an instance to a serving group

Instances of one group share one state database, so they must be on a server engine (step 1 for that service) before a second one joins. Point the new instance at the same database and the same group; it serves the same published definitions, and the console lists it beside the first.

The test of the first step is `ConsoleScaleOutTests` (`DbDataKit.Server.Tests`, SQL Server and PostgreSQL): a console with a person, a skipped checklist step, a failing block check and its alert is moved with the command above and the restarted console has all of it.
