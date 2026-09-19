import { createRouter, createWebHistory } from "vue-router";
import { useFloorStore } from "./stores/floor";

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/enroll", component: () => import("./views/EnrollView.vue") },
    { path: "/login", component: () => import("./views/LoginView.vue") },
    { path: "/unlock", component: () => import("./views/UnlockView.vue") },
    { path: "/hand-in", component: () => import("./views/HandInView.vue") },
    { path: "/warehouses", component: () => import("./views/WarehousePickView.vue"), meta: { auth: true, home: true } },
    { path: "/map", component: () => import("./views/MapAisleView.vue"), meta: { auth: true, home: true } },
    { path: "/tasks", component: () => import("./views/TaskListView.vue"), meta: { auth: true, home: true } },
    { path: "/tasks/:id", component: () => import("./views/TaskDetailView.vue"), meta: { auth: true, task: true } },
    { path: "/sync-issues", component: () => import("./views/SyncIssuesView.vue"), meta: { auth: true } },
    { path: "/", redirect: "/tasks" },
  ],
});

router.beforeEach(async (to) => {
  const floor = useFloorStore();
  if (!floor.loaded) {
    await floor.hydrate();
  }
  if (!floor.enrolled && to.path !== "/enroll") {
    return "/enroll";
  }
  if (floor.revoked && to.path !== "/hand-in" && to.path !== "/login") {
    return "/hand-in";
  }
  if (to.meta.auth && !floor.unlocked) {
    return floor.sessions.length > 0 ? "/unlock" : "/login";
  }
  if (floor.idleLocked && to.meta.auth) {
    floor.lock();
    return "/unlock";
  }
  return true;
});
