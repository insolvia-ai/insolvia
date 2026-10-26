# Insolvia

Bankruptcy case preparation and electronic filing for consumer-bankruptcy law
firms.

## Language

### Where Insolvia works

**Launch set**:
The states whose bankruptcy courts Insolvia verifies its filing for and names as where it files first. It is a promise, never a limit on which cases can be prepared.
_Avoid_: launch states, supported states

**Exemption coverage**:
The states whose exemption figures Insolvia keeps checked against statute, and so can complete Schedule C for. It is decided separately from the launch set and may be wider.
_Avoid_: supported states, exemption states

### Data and launch

**First real data**:
The moment a real firm's case data first exists in prod, whether in a pilot or after launch. Before it, prod data is disposable and a reshape is a reseed; after it, a reshape is a migration.
_Avoid_: launch (when the reshape deadline is meant), go-live
