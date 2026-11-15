# Images used by the top-level README

## Present

Three screenshots captured on the reference machine, named by capture time because their content was
not described when they were added: `app-214238.png`, `app-214257.png`, `app-214312.png`.

They are **3833 x 2073 full-desktop captures**, not window crops. That matters: at the width a README
renders (~900 px), a full-desktop capture scales down by 4x and the labels become unreadable, and the
desktop background is visible. Worth re-capturing as window-only crops when convenient.

| File | Referenced in README | Shows |
|---|---|---|
| `app-214257.png` | yes, as the front-door figure | assumed Interrupts page - **confirm** |
| `app-214238.png` | no | unknown |
| `app-214312.png` | no | unknown |

To finish this properly: say which file shows which screen, then rename them (`app-interrupts.png`,
`app-home.png`, `app-check.png`) and reference the useful ones.

## How to capture a better one

1. Launch `StutterGuard.exe`, apply a plan, restart, open the screen you want.
2. **Alt+PrintScreen** for the active window only - not PrintScreen, which takes the whole desktop.
3. Save as PNG at native resolution.
4. Drop it in this folder and reference it from `README.md`.
