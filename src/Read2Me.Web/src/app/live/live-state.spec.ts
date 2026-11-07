import { IDLE_ASSEMBLY, applyAssembly } from './live-state';
import { assemblyMessage } from '../../testing/live-messages';

describe('applyAssembly', () => {
  it('records the folder a run belongs to and clears the previous output', () => {
    const previous = { ...IDLE_ASSEMBLY, folder: 'old', outputFileName: 'Old.m4b' };

    const state = applyAssembly(
      previous,
      assemblyMessage({
        kind: 'PhaseStarted',
        phase: 'Gather',
        folder: 'dracula',
      }),
    );

    expect(state).toMatchObject({
      isRunning: true,
      currentPhase: 'Gather',
      folder: 'dracula',
      outputFileName: null,
    });
  });

  it('keeps the output file name a completed run announces', () => {
    const running = {
      ...IDLE_ASSEMBLY,
      isRunning: true,
      currentPhase: 'Finalize' as const,
      folder: 'dracula',
    };

    const state = applyAssembly(
      running,
      assemblyMessage({ kind: 'Completed', folder: 'dracula', outputFileName: 'Dracula.m4b' }),
    );

    expect(state).toMatchObject({
      isRunning: false,
      currentPhase: null,
      encodePercent: 100,
      folder: 'dracula',
      outputFileName: 'Dracula.m4b',
    });
  });

  it('keeps the known folder when a message does not name one', () => {
    const running = { ...IDLE_ASSEMBLY, isRunning: true, folder: 'dracula' };

    expect(
      applyAssembly(running, assemblyMessage({ kind: 'Progress', fraction: 0.5 })),
    ).toMatchObject({
      folder: 'dracula',
      encodePercent: 50,
    });
    expect(applyAssembly(running, assemblyMessage({ kind: 'Cancelled' }))).toMatchObject({
      isRunning: false,
      folder: 'dracula',
      outputFileName: null,
    });
  });
});
