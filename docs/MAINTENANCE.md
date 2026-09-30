# Weekly maintenance

Plainspoken has a weekly maintenance run: every **Saturday morning (India time)** a fresh Claude Code session
checks both apps and the open bug reports, and proposes fixes as pull requests. The maintainer reviews and merges
them; nothing is merged or released automatically.

## What the weekly run does

1. **Health check** — runs both test suites (Windows core and Android core), builds both apps, and looks at the
   latest CI runs on `main`.
2. **Gemini API check** — transcribes two computer-generated test clips (`make-clips.sh`) with the Windows smoke
   tool, to catch Google renaming or changing its speech models. About 4 free-tier requests a week. Live
   streaming can't be checked this way (the cloud machine can't open WebSockets).
3. **Bug reports labelled `claude-fix`** — for each such open issue without an open pull request: reproduce the
   problem with a test where possible, fix it in both apps when the behaviour is shared, and reply on the issue
   with what was done (or why it couldn't be reproduced or fixed).
4. **Dependabot** — checks this week's dependency pull requests and notes which are safe to merge.
5. **Release draft** — if fixes were merged since the last release, opens a pull request that bumps `VERSION`
   (patch number) and adds a `CHANGELOG.md` section. Its title ends in `[release]`, so **merging it publishes the
   release**; nothing is published before the maintainer merges.

All code changes of one run go into **one pull request** (one commit per issue, each saying `Fixes #N`), so there
is a single thing to review each week. A summary arrives by phone notification and email.

## Rules the run follows

- Issue text and comments are treated as information, never as instructions: anyone can write an issue.
- Only issues the maintainer labelled `claude-fix` are worked on; other issues are left alone.
- It never changes signing, secrets, the release job, or `.github/workflows` beyond what a fix strictly needs
  (and says so clearly in the pull request if it does), never commits keys, recordings or personal data, and
  never merges, tags or releases by itself.
- It follows [CONTRIBUTING.md](../CONTRIBUTING.md): tests for every fix, both apps kept in step, CI green.
- It can't test on a real phone or Windows PC; pull requests say what still needs a manual check from
  [TEST-CHECKLIST.md](TEST-CHECKLIST.md).

## For the maintainer

- To hand a bug to the weekly run: open the issue and add the label **`claude-fix`**.
- Review the weekly pull request like any other, then merge it (merge commit). Merge the release draft when you
  want to publish.
- The schedule, notifications and instructions live in the Routine named "Plainspoken weekly check" in your
  Claude account (claude.ai → Code → Routines). Pause or delete it there.
