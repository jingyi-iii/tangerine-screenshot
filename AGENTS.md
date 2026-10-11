# tangerine-screenshot

A hotkey-first screenshot tool for Windows that lives in the tray.  Press a key, grab a window, a region or the whole screen, and the shot lands on a card in the corner ready to copy, save or drag into another app.

## Layout

| Path | Purpose |
| --- | --- |
| `screenshot/App.xaml(.cs)` | Startup, wiring, single-instance-less tray lifetime |
| `screenshot/OverlayWindow` | Region selection: dim mask, window snapping, loupe, size chip |
| `screenshot/PreviewWindow` | The shot card: copy, drag-out, archive, auto-dismiss |
| `screenshot/ViewerWindow` | Full-screen view of one shot |
| `screenshot/IntroWindow` | The one-time "press Ctrl+Shift+S" pill |
| `screenshot/TrayMenuWindow` | The themed tray menu, light/dark aware |
| `screenshot/Services/` | Capture orchestration, GDI capture, tray, sound, logging, P/Invoke |
| `screenshot/Themes/` | The two theme dictionaries and the live theme manager |
| `scripts/` | Publish, clean, asset generation, tray-menu probe |
| `docs/` | The tray-menu figure the README embeds |

## Language conventions

- Conversation with the user: Chinese.
- All generated artifacts and files (including this `AGENTS.md`): English.

## README style

`README.md` follows the house style of the other `jingyi-iii` repositories
(`begonia-os`, `begonia-pciesim`, `tangerine-github-access`, `tangerine-nudge`):

- the title is `# <repo-name>` alone, with no tagline on the same line;
- two or three short prose paragraphs open it: what it is, what works today, what is unusual
  about it. No bullet list and no bold `**Status:**` line up front;
- one table follows, here the layout table;
- then `## Install`, `## Use`, `## Notes`, `## License`, in that order. `## How it
  works` may be inserted between `Use` and `Notes` when there is something worth
  explaining;
- commands in a block carry `#` comments that state their cost (size, minutes);
- `## Notes` states the limitations, why the large artefacts are untracked, the licences of
  the dependencies, and the development environment, in the first person and plainly;
- `## License` is that heading followed by `MIT` and nothing else;
- no badges, no emoji, no table of contents, hard wrap at 100 columns.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/), in English, with the subsystem as
the scope:

```
<type>(<scope>): <imperative summary, <= 50 chars, no trailing period>

<body: why the change is needed, not what the diff already shows; wrap at 72>

<footer: BREAKING CHANGE: ..., Closes #12, Refs: ...>
```

- `type`: `feat`, `fix`, `build`, `docs`, `refactor`, `perf`, `test`, `chore`,
  `ci`, `style`, `revert`
- `scope`: `app`, `overlay`, `preview`, `tray`, `capture`, `themes`, `tools`, `docs`, `repo`
- One logical change per commit.
- Mark incompatible changes with `!` after the scope, or with a `BREAKING CHANGE:` footer.
- `.gitmessage` holds the template; `git config commit.template .gitmessage` makes
  `git commit` open the editor with it.