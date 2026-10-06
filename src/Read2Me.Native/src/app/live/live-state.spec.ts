import { IDLE_ASSEMBLY, applyAssembly } from './live-state';

describe('applyAssembly', () => {
  it('records the folder a run belongs to and clears the previous output', () => {
    const previous = { ...IDLE_ASSEMBLY, folder: 'old', outputFileName: 'Old.m4b' };

    const state = applyAssembly(previous, {
      kind: 'phaseStarted',
      phase: 'Gather',
      folder: 'dune',
    });

    expect(state).toMatchObject({
      isRunning: true,
      currentPhase: 'Gather',
      folder: 'dune',
      outputFileName: null,
    });
  });

  it('keeps the output file name a completed run announces', () => {
    const running = { ...IDLE_ASSEMBLY, isRunning: true, currentPhase: 'Finalize', folder: 'dune' };

    const state = applyAssembly(running, {
      kind: 'completed',
      folder: 'dune',
      outputFileName: 'Dune.m4b',
    });

    expect(state).toMatchObject({
      isRunning: false,
      currentPhase: null,
      encodePercent: 100,
      folder: 'dune',
      outputFileName: 'Dune.m4b',
    });
  });

  it('keeps the known folder when a message does not name one', () => {
    const running = { ...IDLE_ASSEMBLY, isRunning: true, folder: 'dune' };

    expect(applyAssembly(running, { kind: 'progress', fraction: 0.5 })).toMatchObject({
      folder: 'dune',
      encodePercent: 50,
    });
    expect(applyAssembly(running, { kind: 'cancelled' })).toMatchObject({
      isRunning: false,
      folder: 'dune',
      outputFileName: null,
    });
  });
});
