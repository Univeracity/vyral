# Temporal coordinator replay fixtures

`vyral-run-coordinator-v1-legacy-completion.json` is a completed
`Vyral.RunCoordinator.v1` history captured with Temporal .NET SDK 1.17.0 from the coordinator shape
that existed before Vyral added its continue-as-new patch marker. Its workflow input and activity
payload use only synthetic `fixture-run` data.

Worker, build, and Temporal run identifiers were replaced with fixed fixture values after capture;
the event sequence, command attributes, and payloads were not changed. The normal test suite replays
this history offline with the current coordinator. Do not regenerate it merely to make an
incompatible coordinator change pass: retain the required patch branch or introduce a new workflow
type/version. A negative-control replay with an intentionally different command stream proves the
fixture would detect nondeterminism rather than passing vacuously.
