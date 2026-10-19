## Summary

- Describe the user-facing or operational change.

## Related Issues

- Closes #
- Related to #

## Validation

- [ ] `dotnet build OpenWebUI.slnx -c Release` clean
- [ ] `dotnet test tests/OpenWebUI.Api.Tests` green locally (or a note on what only CI covers)
- [ ] Production-code changes include a new or updated test in this PR
- [ ] `.razor` class changes regenerated `src/OpenWebUI.Client/wwwroot/css/tailwind.css`
      (`tailwindcss -i src/OpenWebUI.Client/tailwind.input.css -o src/OpenWebUI.Client/wwwroot/css/tailwind.css --minify`) and committed
- [ ] Drift guards pass: `python3 tools/check-css-classes.py`,
      `check-form-a11y.py`, `check-i18n-parity.py`, `check-spec-status.py`
- [ ] New env vars documented in `.env.exemplo` (`./scripts/check-env-drift.sh`)

## Tests Added Or Updated

- List every changed or added test file.
- If no production code changed, state that here.

## Upstream Parity

- Does this match `open-webui/open-webui` behavior? Reference the upstream commit/path if porting.
- If it's a fork-only feature, say so.

## Reviewer Notes

- Risky areas, migrations, feature flags, or manual validation reviewers should know about.
