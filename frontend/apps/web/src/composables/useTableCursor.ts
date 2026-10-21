import { ref, type Ref } from "vue";

export function useTableCursor(rowCount: Ref<number>) {
  const cursor = ref(-1);
  const selected = ref(-1);

  function move(delta: number): void {
    if (rowCount.value === 0) {
      cursor.value = -1;
      return;
    }
    const next = cursor.value < 0 ? 0 : cursor.value + delta;
    cursor.value = Math.min(Math.max(next, 0), rowCount.value - 1);
  }

  function onKeydown(event: KeyboardEvent): void {
    if (event.key === "ArrowDown") {
      event.preventDefault();
      move(1);
      return;
    }
    if (event.key === "ArrowUp") {
      event.preventDefault();
      move(-1);
      return;
    }
    if (event.key === "Enter" && cursor.value >= 0) {
      event.preventDefault();
      selected.value = cursor.value;
    }
  }

  return { cursor, selected, onKeydown };
}
