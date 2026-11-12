# Licence change

**Date:** 2026-11-12
**Applies from:** v1.0.0 onward

## What changed

Stutter Guard previously shipped under "GPLv3 with a Non-Commercial Rider" - a GPLv3 body with a
custom preamble forbidding commercial use.

That combination was self-contradictory. GPLv3 section 10 states:

> You may not impose any further restrictions on the exercise of the rights granted or affirmed
> under this License.

and the licence's own opening line says the document may not be changed. A non-commercial restriction
bolted onto GPLv3 is therefore not a stricter licence; it is an inconsistent one. In practice it left
open the question of whether the GPL grants commercial rights (rider void) or the whole grant fails
for uncertainty - and GitHub classified the repository as `NOASSERTION`, not GPL-3.0.

## What it is now

- **`LICENSE`** is the verbatim GNU General Public License version 3. Unmodified.
- **`COPYRIGHT`** records the copyright holder, the GPL grant, the commercial-licensing option, the
  contribution terms and the trademark note.

Commercial use is permitted under the GPL, with its usual obligation to publish the corresponding
source when you distribute. Organisations that cannot meet that obligation can obtain a separate
commercial licence in writing on request.

## Important: this is not retroactive

Anyone who obtained Stutter Guard v1.0.0 while the old `LICENSE` file was in place keeps whatever
rights that version granted them. Relicensing binds new distributions only. No attempt is made - and
none could be made - to claw back permissions already given.

## Why this was possible

Kareem Shabaka is the sole copyright holder. There are no third-party contributors whose permission
would be required, and no vendored third-party source in the tree.

## What did not change

Nothing about how the software works, what it collects, or what it does to a machine. This is a
licensing change only. The binary is unaffected.