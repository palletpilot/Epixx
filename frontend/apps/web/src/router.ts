import { createRouter, createWebHistory } from "vue-router";
import { useAuthStore } from "./stores/auth";
import { useTenantStore } from "./stores/tenant";

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/", redirect: "/login" },
    { path: "/signup", component: () => import("./views/SignupView.vue") },
    { path: "/verify", component: () => import("./views/VerifyView.vue") },
    { path: "/login", component: () => import("./views/LoginView.vue") },
    { path: "/choose-tenant", component: () => import("./views/ChooserView.vue") },
    { path: "/accept-invite", component: () => import("./views/AcceptInviteView.vue") },
    {
      path: "/app",
      component: () => import("./views/AppShell.vue"),
      meta: { auth: true },
      children: [
        { path: "", redirect: "/app/warehouses" },
        { path: "warehouses", component: () => import("./views/WarehousesView.vue") },
        { path: "tasks", component: () => import("./views/TasksView.vue") },
        { path: "devices", component: () => import("./views/DevicesView.vue") },
        { path: "users", component: () => import("./views/UsersView.vue") },
        { path: "sso", component: () => import("./views/SsoView.vue") },
      ],
    },
    {
      path: "/trial-expired",
      component: () => import("./views/TrialExpiredView.vue"),
      meta: { auth: true },
    },
  ],
});

router.beforeEach(async (to) => {
  const auth = useAuthStore();
  if (to.meta.auth && !auth.isAuthenticated) {
    return "/login";
  }
  if (to.meta.auth && auth.isAuthenticated) {
    const tenant = useTenantStore();
    if (!tenant.context.lifecycle_state) {
      await tenant.refresh();
    }
    if (tenant.isTrialExpired && to.path !== "/trial-expired") {
      return "/trial-expired";
    }
  }
  return true;
});
