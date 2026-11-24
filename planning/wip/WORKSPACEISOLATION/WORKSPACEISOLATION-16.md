# WORKSPACEISOLATION-16 (work in progress)

## Built so far

- nothing yet: the console stores the password only, the refusal of `prod` goes, tests and docs follow.

## Defaults taken

- The password keeps its name `dbdatakit:runner:dev-login:<package>.<channel>.<slot>:password`. Example: Ana sets up `crm` of `crm-to-dw` for `dev`;
  the Runner holds one secret, `dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password`. The answered option B of P25 also named the connection copy's
  password (`dbdatakit:runner:connection:<copy>:password`); the copy is written by AUTHORING-19, so the name stays until then.
- The console's answer gives the user back as plain text (`"user": "ana_dev"`) and names one secret, the password. Example: the screen and AUTHORING-19
  read the user from the answer to write it into the connection's copy.
