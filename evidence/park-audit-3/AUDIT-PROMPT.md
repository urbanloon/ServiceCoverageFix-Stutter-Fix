# Park-heavy service coverage audit

Start from verified Service Coverage Stutter Fix 0.3.6 at `6c4c7f4b5c238698d286bff31026ff11ec760b66`. Preserve it on its existing branch. Work offline using read-only copies of the supplied saves, game assemblies and traces.

As lead, run independent high-effort Astra audits of the Apply merge, Prepare/Clear/Process, the preview and coverage-only pathfinding pipeline, and historical main-thread waits. Assign a separate Astra verifier for native comparisons and a separate high-effort Astra safety reviewer with authority to veto shipping. As lead, own the results and resolve disagreements with measurements or a conservative rejection.

1. Identify what can make the simulation wait in a park-heavy city with a replacement worker active. Grade each inference by the capture, game and mod version, and by whether the waiting thread, readying thread or only a CPU sample was observed.
2. Try competing fixes on the same inputs as vanilla and 0.3.6. Measure small and large workloads, realistic counts of unique road destinations, local and citywide overlap, equal priorities and exhausted budgets. Report slow cases and instrumentation cost. Carry forward only improvements that justify their runtime and maintenance costs.
3. Give the safety reviewer each exact proposed diff. Require matched coverage bytes and ordering of observable writes. Exercise NaNs, signed zero, subnormals, aliased targets, overlapping ranges, empty inputs, city switches, pending jobs, failed scheduling, removal and reload as applicable. Preserve vanilla service timing, dependency chains, temporary data ownership, random-state consumption and saving. Never modify the user's saves.
4. Reject an optimization if its claimed benefit disappears under a representative workload, its correctness depends on an unverified game invariant, or its risk outweighs its gain. An unchanged release is an acceptable result. Keep offline experiments outside the production assembly.
5. Deliver the accepted source, a compile-ready Visual Studio project, measured before/after results, the safety verdict, and the precise limits of the evidence. No new in-game testing is required of the user.
