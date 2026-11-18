# SUBSCRIPTIONS: scheduled deliveries on Work

## Goal

Subscriptions to pages and custom views run as Runner tasks: a subscription is rendered as the subscriber through path 2 and sent by the delivery service;
a subscription stops, telling the person and the owner, when the person loses access. (Reads of outside report catalogs are the subject REPORTCATALOGS.)

## What exists today

- Subscriptions are rows of the console's `delivery` module; the Gateway's `GatewaySubscriptions` (a `BackgroundService`) renders them where the views
  module runs, and the console's sender sends (`ConsoleDelivery.cs`); never verified against a real console and Gateway sharing one SQL Server.
- A subscription whose person lost a rule's access is not stopped by a rule check (only by what the renderer or the identity home says).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| SUBSCRIPTIONS-1 | Subscriptions render on Work: a first-party package with the `views-export` task renders each due subscription as the subscriber through path 2 and hands the PDF to the delivery outbox; `GatewaySubscriptions` goes | TRUSTZONES-10 | M | with the fake channel and clock: two subscribers of one page with different rules receive different rows; the Gateway runs no subscription loop; a run against a real console, Gateway and Runner on one SQL Server (servers suite) |
| SUBSCRIPTIONS-2 | Access loss stops a subscription: a person who loses a rule's access gets one message, the owner one, and nothing more is sent | SUBSCRIPTIONS-1 | S | on three engines: a subscriber removed from the rules' group gets one "stopped" message and no attachment |
| SUBSCRIPTIONS-3 | Scheduled and emailed custom pages: PDF attachment, optional inline PNG, one link to the live page; a failed render tells the owner | SUBSCRIPTIONS-1, PAGEFORMAT-7 | M | a subscription to a custom page delivers the PDF as the subscriber; a render that fails tells the owner |

## Needs from other subjects

- TRUSTZONES-10: governed reads through the linked Gateway as a named person.
- PAGEFORMAT-7: custom pages as pages, before SUBSCRIPTIONS-3.

## Proposed decisions

None.

## Shared areas touched

- `DbDataKit.Delivery`, `DbDataKit.Views.Export`.
- `docs/ui-editors.md`, `docs/views.md`.
