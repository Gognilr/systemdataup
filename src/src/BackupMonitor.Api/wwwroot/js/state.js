/* 共享注册表（UI-REDESIGN §9 模块拆分）：
   App —— 全局运行时状态；ACTIONS/LOADERS —— 动作与列表加载器注册表。
   各视图模块在求值期向这里注册，app.js 统一暴露为 window 全局供内联处理器使用。 */
export const App = { user: null, timer: null, state: {} };
export const ACTIONS = {};
export const LOADERS = {};
