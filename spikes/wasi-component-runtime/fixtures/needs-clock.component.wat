;; Wie needs-random, nur für wasi:clocks/monotonic-clock. `instant` ist ein u64-Alias, deshalb
;; lässt sich der benötigte Ausschnitt der Schnittstelle direkt hinschreiben.
(component
  (import "wasi:clocks/monotonic-clock@0.2.6" (instance
    (export "now" (func (result u64)))))
  (core module $module
    (func (export "run") (param i32) (result i32)
      local.get 0))
  (core instance $instance (instantiate $module))
  (func $run (param "value" s32) (result s32)
    (canon lift (core func $instance "run")))
  (export "run" (func $run)))
