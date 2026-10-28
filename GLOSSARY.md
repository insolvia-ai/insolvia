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

### Cases and chapters

**Case**:
One bankruptcy matter at one court, from retention through closing. Once filed it is one court case with one case number, whatever chapter it is in.
_Avoid_: matter, file

**Conversion**:
A court event that moves a filed case from one chapter to another. The case, its number and its petition date stay the same.
_Avoid_: chapter change (for a filed case), re-filing

**Refiling**:
A new case for the same client after an earlier case was dismissed, with its own case number and petition date.
_Avoid_: conversion, reopening

**Confirmation**:
The court's ruling on a Chapter 13 plan, approving or denying it: a dated event within a filed case, never a status. Not a person confirming a value (confirm-before-entry).
_Avoid_: confirmed status

**Petition chapter**:
The chapter a case was filed under. It never changes; the petition packet belongs to it.
_Avoid_: original chapter, filing chapter

**Current chapter**:
The chapter a case is in now: before filing, the one being prepared; after filing, the petition chapter or the latest conversion's.
_Avoid_: case chapter (when the petition chapter is meant)

### Chapter 13 plans

**Plan version**:
One Chapter 13 plan as a whole document: a draft while it is being prepared, then fixed once it is filed with the court. A case's plans are an ordered series of versions.
_Avoid_: plan record (for a filed plan), revision

**Plan amendment**:
A plan version filed to replace a proposed plan before the court has confirmed one (§ 1323).
_Avoid_: amended schedule (Rule 1009 is for schedules, not plans)

**Plan modification**:
A plan version filed to change a confirmed plan (§ 1329).
_Avoid_: plan amendment (after confirmation)

**Confirmed plan**:
The plan version the court's latest confirmation in the current Chapter 13 period approved. It binds the debtor and creditors until a modification is confirmed.
_Avoid_: active plan, current plan

**Chapter 13 period**:
The stretch of a filed case spent in Chapter 13, from its petition or a conversion into 13 until a conversion out. Plan versions and confirmations count only within their period.
_Avoid_: Chapter 13 phase

### Firms and staff

**Firm user**:
A person's membership of one firm as staff, carrying that firm's role and permissions. One login may be a firm user at several firms, and each membership is separate.
_Avoid_: staff account, member

**Chosen firm**:
The one firm a request acts in. A person with more than one firm names it; a person with one firm need not.
_Avoid_: active firm (a firm's status), current firm, tenant

### Clients and the portal

**Client**:
A person a firm represents or may represent, kept once per firm. One client may have many cases over time; a joint case is two clients.
_Avoid_: debtor (for the firm's record of the person), customer

**Debtor**:
A client as named in one case, in a filing role. The case keeps its own copy of the client's identity.
_Avoid_: client (when the person on one case is meant)

**Portal login**:
A person's own sign-in to the client portal: one per email address, shared by every firm that invites them. On its own it grants nothing.
_Avoid_: portal account, client account

**Portal access**:
A client's standing permission, at one firm, to use the portal for their cases. Each firm's portal access is separate; nothing one firm holds reaches another.
_Avoid_: binding, portal account
