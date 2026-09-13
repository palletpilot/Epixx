export function isAtRest(state: {
  home: boolean;
  taskOpen: boolean;
  formDirty: boolean;
  printInFlight: boolean;
  outboxFlushed: boolean;
  upgradeBlocked: boolean;
  offline: boolean;
}): boolean {
  if (!state.home || state.taskOpen || state.formDirty || state.printInFlight) {
    return false;
  }
  return state.outboxFlushed || state.upgradeBlocked || state.offline;
}

export async function applyUpdate(ready: boolean, atRest: boolean, apply: () => Promise<void> | void): Promise<boolean> {
  if (!ready || !atRest) {
    return false;
  }
  await apply();
  return true;
}
