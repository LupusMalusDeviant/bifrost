;; Importiert wasi:random/random — sonst identisch zu no-import. Der Export braucht den Import
;; nicht; geprüft wird allein, ob das Component ohne Random-Grant überhaupt instanziiert werden
;; kann (deny-before-instantiation, WP3.2).
(component
  (import "wasi:random/random@0.2.6" (instance
    (export "get-random-u64" (func (result u64)))))
  (core module $module
    (func (export "run") (param i32) (result i32)
      local.get 0))
  (core instance $instance (instantiate $module))
  (func $run (param "value" s32) (result s32)
    (canon lift (core func $instance "run")))
  (export "run" (func $run)))
