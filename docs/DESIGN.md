# Design Notes

## Scaling to 1,000 bets per second

First measure where time is spent and load-test the PostgreSQL transaction path. Keep wallet and
event transactions short and retain row-level locking for correctness. If a single event escrow
becomes a hot row, shard its escrow into a fixed set of subaccounts and aggregate them for
settlement. For sustained higher write volume, consider partitioning ledger history, a connection
pooler, and read replicas for read-only queries. The transactional outbox currently records
state-change events atomically and its worker logs claimed event ids and types; add a broker
adapter and consumer idempotency before relying on external delivery. Large settlements can move
to an outbox-backed, resumable worker with explicit per-event progress; that changes the current
single-transaction atomicity model and needs reconciliation and recovery tests before adoption.

## Reconciling an external provider

Fetch the provider's settlement or deposit report and match each provider reference and amount to
the local deposit record and its clearing-account ledger posting. Classify matched items, items
present only locally, items present only at the provider, and amount mismatches. Persist exceptions
for review; never edit or delete ledger entries to force a match. Correct confirmed discrepancies
with audited compensating postings after review.

## Before going live

This service is play-money only and is not ready to process real funds. Before any production
launch, add operational monitoring and alerts for ledger imbalance, failed deposits/settlements,
webhook authentication failures, latency, database lock waits, and service health. Establish
backups and restore drills, secrets management and rotation, security and load testing, incident
runbooks, and audit retention/tamper-evidence. Add fraud and risk controls such as stake/exposure
limits, velocity checks, anomaly detection, and withdrawal review. Obtain jurisdiction-specific
legal, licensing, KYC/AML, consumer protection, and responsible-gambling guidance before enabling
real-money activity.
