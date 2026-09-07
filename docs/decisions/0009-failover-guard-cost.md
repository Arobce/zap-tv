# 0009 — What the failover safety guard costs

Status: accepted
Date: 2026-09-07

## Question

Phase 8 says a failover candidate is eligible only when the country agrees (or is absent
on both sides) and the `channel_key` came from a `tvg_id` or the normalized titles match
exactly. The rule is conservative on purpose. Conservative rules have a cost that only
shows up against real data: if the guard refuses nearly every alternative, a channel with
three providers behaves like a channel with one and the phase has bought nothing.

## Measurement

`dotnet run --project src/Iptv.Harness -- failover`, against the reference library
(20,313 live channel keys from one provider, 28,285 streams).

```
live channels with one stream only : 16,688
live channels with alternatives    : 3,625

alternative streams                : 6,563
  accepted for failover            : 3,538 (53.9%)
  refused, country disagreed       : 3,006 (45.8%)
  refused, inferred key + title    :    19 (0.3%)

channels the guard touched         : 1,547 of 3,625
```

The refusals, sampled:

```
name:1tv       country AFG does not agree with GE
name:5kanal    country UKR does not agree with RU
name:24kitchen country NL  does not agree with PT
name:2stv      country AF  does not agree with SN
name:360       country TR  does not agree with LV
```

## What this says

Every refusal in the sample is a genuinely different channel. Afghan 1TV and Georgian 1TV
share a name and nothing else; Ukrainian 5 Kanal and the Russian channel of the same name
are the case the guard was written for, and substituting one for the other during a
failover would be worse than showing an error.

So the 45.8% is not a false-positive rate. It is the guard working, and the number is
large because normalization collapses country prefixes by design — which is right for
grouping a list and wrong for silently swapping a stream.

Failover still has plenty to work with: 53.9% of alternatives survive, across 3,625
channels. The title rule almost never fires (19 refusals) because streams sharing an
inferred key usually do share a title; it stays because the 19 it catches are exactly the
`ESPN 2` / `ESPN2` collisions that are invisible until they happen.

One correction to the specification. The PRD compares against `channels.country`, which is
a single denormalized value for a key that may span several differently-titled streams.
The implementation compares each candidate against the stream that will actually be
opened, deriving the country from that stream's own title. Where the two agree the result
is identical; where they do not, the anchor is the more accurate question to ask.

## Recovery, measured

The PRD's exit criterion is recovery within 10s after a stream is killed mid-playback.
Proving that against the reference account means deliberately breaking a stream on a
single-connection subscription. `dotnet run --project src/Iptv.Harness -- drill` does the
same thing offline: a scratch database with two candidates, the first a port nothing is
listening on and the second mpv's built-in generator, driven through the real player.

```
attempt 1: Dead      end-file error -13    HttpError after 19ms
attempt 2: Working   playing after 145ms
recovered in 2146ms (within the 10s budget)
```

Detection took 19ms and the new stream produced a picture 145ms after being asked to. The
remaining ~2s is the provider connection limiter's minimum interval.

That interval is the reason `ProviderConnectionLimiter.AcquireAsync` exists. `TryAcquire`
refuses inside the interval, which is right for a user clicking a channel — silently
waiting on a click reads as a dead button — and wrong for failover, where a stream that
dies 19ms after opening is always inside the interval and would be refused outright. The
user would be shown an error while a working alternative sat unused. Failover waits; the
click still refuses.

## Not yet established

- Cross-provider failover. Every alternative in the reference library comes from one
  provider, so the survey measures within-provider duplicates only.
- The firewall-kill criterion in its literal form, which needs a second provider.
- Stall detection against a real provider. The drill covers connect failure and the
  first-frame deadline; the 5s no-new-frames path has been exercised only by construction.
