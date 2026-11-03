// CPU-time gates for the 10 ms Workers free-tier budget (FR-ONL-09, QUALITY_GATES V5.4). The tests run in local workerd, where
// only wall time is available (1 ms resolution) and other test files run in parallel on the same cores. A load spike inflates
// a median; a slow parser inflates every median. So a measurement is repeated up to three times, 21 runs each after a
// warm-up, and the best median is what is checked against the unchanged 10 ms budget.

export const CPU_BUDGET_MS = 10;

export interface Timing {
  medianMs: number;
  maxMs: number;
  attempts: number;
}

export async function bestMedian(run: () => unknown): Promise<Timing> {
  for (let i = 0; i < 3; i++) await run();
  let best: Timing | null = null;
  for (let attempt = 1; attempt <= 3; attempt++) {
    const times: number[] = [];
    for (let i = 0; i < 21; i++) {
      const start = performance.now();
      await run();
      times.push(performance.now() - start);
    }
    times.sort((a, b) => a - b);
    const timing = { medianMs: times[10]!, maxMs: times[20]!, attempts: attempt };
    if (best === null || timing.medianMs < best.medianMs) best = timing;
    if (best.medianMs < CPU_BUDGET_MS / 2) break;
  }
  return best!;
}
