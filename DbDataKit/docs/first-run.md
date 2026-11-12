# First run and the setup checklist

A new console has no user, and it is closed until the first administrator comes in. This page is how that happens and what to do next.

## The setup link

While the console has no user it makes a **setup link** and writes it to the log and to `FIRST-RUN.txt` in its directory (`--repo`, or `DBDATAKIT_DATA_SERVER`). The link is a whole address, `https://<your console>/invite#<code>`; the code is in the part after `#`, which a browser never sends to a server and an access log never records.

Open it once and register a passkey. That person is the first administrator, and from then on the link is dead: it works once.

- **It ends.** A setup link is good for 24 hours. The setting is `setup.link_lifetime_hours` in the console's `settings.yaml` (1 to 168):

  ```yaml
  setup:
    link_lifetime_hours: 24
  ```

  A value that is not a number from 1 to 168, or a key that is not a setup setting, stops the command and the console with a message that names the file and the line.
- **A new one is made on the machine.** If the link ended or was lost, run this on the machine the console runs on:

  ```
  dbdatakit setup link --url https://localhost:8480
  ```

  It prints the new link, writes it to `FIRST-RUN.txt` and makes every unused link stop working, so only the newest opens. `--url` is the address people open the console at (the default is the console's own listen address); `--repo` names the console's directory when it is not the default. The audit records that a setup link was made, when it ends and which account ran the command, never the code.
- **Only on the machine.** There is no web route that makes a setup link, so nothing remote can ask for one. Once the console has a user the command refuses, and says that to let someone else in you make an invitation (`dbdatakit user invite --email <address>`, or the invitation form on the Users page).

A refused invitation says only that it is not valid, used or expired, so a person guessing learns nothing; it also names `dbdatakit setup link`.

## The setup checklist

After you sign in, **Setup** in the rail (administrators) lists the first steps, and **Home** says how many are left:

| Step | Opens | Finished when |
| --- | --- | --- |
| Choose how people sign in | Users | a person has a passkey (or another way in) and is enabled |
| Pair your first server | Servers | a server is paired |
| Add your first connection | Connections | a Sync server or a Gateway has a connection |
| Make a first Build project | Servers | a Build server hosts a project |
| Set up where alerts are sent | Delivery | a delivery channel is configured |
| Send failed checks to a channel | Quality | the section `quality: alerts:` of `settings.yaml` names a channel this install has |
| Connect a git remote | | not available yet: it is listed so it is not forgotten, and never counts as left |

A step is **finished by what the install has**, never by a click: pairing a server finishes its step whatever was pressed before. **Skip** takes a step off the Home banner; it stays on the list, where **Put back** returns it. A step that is finished stays finished even if it was skipped earlier.

## What is not built yet

A single-machine bundle that installs the console and the servers together and pairs them with nobody typing a code is planned (plan 23a, FQ18). Today each server announces itself to the console and waits for an administrator to confirm it on the Servers page.

### Alerts of failed checks

The servers that evaluate data quality checks (Sync, Build) keep the results; the console watches them and sends a message through a delivery channel **when a check fails and once when it passes again**, not at every evaluation, and not again after the console restarts. Name the channel in `settings.yaml`:

```yaml
quality:
  alerts:
    channel: ops              # a delivery channel (the Delivery page)
    recipients: [data@example.com]
    every_seconds: 60         # how often the servers are asked, at least 10
```

The Quality page says what is set, whether the channel exists and what is wrong with the file. The message names the check, the dataset, the measure and the keys of a few failing rows, and says whether what depends on the dataset waits.
