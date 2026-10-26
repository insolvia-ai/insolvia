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
