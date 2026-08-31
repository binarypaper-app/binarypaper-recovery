# Photographed page corpus

Four photographs of a screen displaying a BinaryPaper page, cropped to the page itself.

They are here because every other image in this repository is synthetic. The conformance vectors are
rendered by a program: flat, evenly lit, exactly square, every module a clean multiple of a pixel.
That is deliberate — a vector has to be reproducible byte for byte — but it means the suite cannot
notice a decoder that reads a rendering perfectly and a photograph not at all. That is not a
hypothetical failure. It is what happened: a decoder that passed every image vector recovered
**zero** codes from real photographs of a real page.

This is the input that catches it.

## What they are

| | |
| --- | --- |
| Source | a phone photograph of a laptop screen showing the page as a PDF |
| Size | 4000 px wide, cropped vertically to the page area |
| Page | 35 symbols, QR version 40 |
| Capsule | 30 source + 5 repair, so 30 of the 35 are needed |
| Together | 32 distinct codes — enough to recover |

Photographs of a *screen*, not of paper, which makes them harder than the intended case rather than
easier: a screen adds moiré and backlight glare that print does not have, and the page is seen
slightly off-square. One frame catches the bottom row that the others cut off, which is why four
photographs recover the capsule and three do not.

## What they are not

**Not conformance vectors, and they could not be.** The suite pins the frame bytes a decoder must
produce once it has read a symbol; it deliberately does not pin how many symbols a detector finds in
a photograph, because two conforming implementations can honestly differ there. These measure a
build. The vectors define the contract. Keeping the two apart is what lets the contract stay
frozen while the reading pipeline keeps improving.

## What they measure

Point the page-yield benchmark at this directory:

```bash
dotnet run --project benchmarks/BinaryPaper.Recovery.PageYield -- benchmarks/page-corpus
```

At the time they were committed, on a desktop machine:

| Strategy | Distinct codes | Time |
| --- | --- | --- |
| plain — image straight to the decoder, one read | 32 | |
| plain, both binarizers | 32 | |
| whole-page sweep | 32 | 2.4 s |
| full pipeline, with rectify-and-retry | 33 | 16.4 s |

The last row is the interesting one, and it is why the reader escalates to rectification only when
the cheap pass has not produced enough: here it buys one extra code for roughly seven times the
work. On a harder corpus it has been the difference between recovering and not. Its value tracks
how much the detector is struggling, not how hard the page looks.

## Provenance

Contributed by the project owner from their own backup, cropped to the page area so that nothing
around it — file paths, window chrome, the desktop — is published, and re-encoded, which removed the
camera metadata the originals carried. The backup they depict holds no personal content.
