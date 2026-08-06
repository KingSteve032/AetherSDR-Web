## Summary

<!-- What problem does this solve, what was the root cause, and what changed? -->

## Validation

<!-- List exact commands and results. Do not claim a test that was not run. -->

- [ ] Relevant .NET tests pass
- [ ] Browser tests pass, or are not affected
- [ ] `dotnet format AetherSDR-Web.slnx --verify-no-changes --no-restore` passes
- [ ] No live-radio or RF test was run

## Safety and compatibility

- [ ] Radio-reported state remains authoritative
- [ ] TX remains fail-closed and requires unambiguous operator intent
- [ ] Untrusted input is validated at its boundary
- [ ] Authentication, authorization, ownership, and session isolation are preserved
- [ ] No secret, credential, live configuration, deployment payload, or generated output is included
- [ ] Protocol, deployment, and operator documentation is updated where needed

## Constitutional citation

<!-- Name the most load-bearing principle when relevant, for example: Principle VII. -->

## Release or deployment impact

<!-- Note configuration, migration, rollback, compatibility, and release implications. -->
