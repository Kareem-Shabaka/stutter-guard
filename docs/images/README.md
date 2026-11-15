# Images used by the top-level README

| File | Size | Aspect | Shows | Where referenced |
|---|---|---|---|---|
| `app-214731.png` | 1697 x 1046 | 1.62 | **LatencyMon Main tab, clean 3 h 51 min run** - confirmed by the author | `## The numbers` |
| `app-214811.png` | 3353 x 1673 | 2.00 | unknown - **currently the front-door figure** | front door |
| `app-214750.png` | 3819 x 1237 | 3.09 | unknown - wide strip, likely a scrolled page or the check table | not used |

## The mistake this set already caused

The LatencyMon capture was first used as the **front-door hero**, under the headline "Your PC is fast.
It stutters anyway." A clean report with a green verdict, directly beneath a claim that the machine
stutters, made the page refute itself - and it showed a competitor's tool as the product's hero image.

Two things followed from that:

1. The headline asserts nothing about the reader's machine. It states a fact about *measurement*:
   average FPS is not the number that decides whether a game feels smooth. That claim cannot be
   contradicted by a screenshot.
2. A clean LatencyMon report is **evidence**, not a hook. It belongs under `## The numbers`, captioned
   as the target the plan produces - with the caveat that the problem case is three orders of magnitude
   worse, which is the point being made.

## To finish

Name `app-214811.png` and `app-214750.png` and this becomes three useful figures:

1. Front door - the Interrupts page with a plan visible (cores, devices, evidence chips).
2. "The numbers" - already filled by the LatencyMon report.
3. "Why interrupt placement is the lever" - Home with its verdict line, or the check results table.

Rename to `app-interrupts.png` / `app-check.png` / `app-home.png` once known, and reference each in its
section.

## Capturing

**Alt+PrintScreen** for the active window only, not PrintScreen. PNG at native resolution. The labels are
the point; a downscaled capture loses them.
