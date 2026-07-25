;; Exports mit den Typen, um die es in dieser Stufe geht: list<u8> und result<T,E>.
;; Beide brauchen die kanonische ABI, also lineares Memory plus realloc — der Grund, warum sich
;; das nicht wie ein (s32)->s32-Export hinschreiben lässt.
(component
  (core module $module
    (memory (export "memory") 1)
    ;; Bump-Allocator: Der kanonische ABI-Vertrag verlangt realloc, um Host-Werte in den Guest zu
    ;; kopieren. Freigeben gibt es hier nicht — für ein Fixture mit einer Handvoll Aufrufen reicht
    ;; ein wachsender Zeiger.
    (global $next (mut i32) (i32.const 1024))
    ;; Gemeinsamer Kern für realloc und die Rückgabebereiche. Rundet auf 8 auf: Die kanonische ABI
    ;; verlangt ausgerichtete Zeiger, und nach einer Liste ungerader Länge stünde $next sonst schief.
    (func $bump (param $size i32) (result i32)
      (local $ptr i32)
      (global.set $next
        (i32.and (i32.add (global.get $next) (i32.const 7)) (i32.const -8)))
      (local.set $ptr (global.get $next))
      (global.set $next (i32.add (global.get $next) (local.get $size)))
      local.get $ptr)
    (func (export "realloc")
      (param $old i32) (param $old_size i32) (param $align i32) (param $new_size i32)
      (result i32)
      (call $bump (local.get $new_size)))

    ;; echo: gibt die empfangenen Bytes unverändert zurück. Beim Export liefert die kanonische ABI
    ;; den Rückgabebereich ZURÜCK (vom Guest alloziert), statt ihn übergeben zu bekommen.
    (func (export "echo") (param $ptr i32) (param $len i32) (result i32)
      (local $ret i32)
      (local.set $ret (call $bump (i32.const 8)))
      local.get $ret
      local.get $ptr
      i32.store
      local.get $ret
      i32.const 4
      i32.add
      local.get $len
      i32.store
      local.get $ret)

    ;; classify: gerade Zahl -> ok("gerade"), ungerade -> err(die Zahl).
    ;; Rückgabebereich: [0] Diskriminante, [4] Zeiger bzw. Fehlerwert, [8] Länge.
    (data (i32.const 16) "gerade")
    (func (export "classify") (param $value i32) (result i32)
      (local $ret i32)
      (local.set $ret (call $bump (i32.const 12)))
      (if (i32.rem_u (local.get $value) (i32.const 2))
        (then
          local.get $ret
          i32.const 1
          i32.store            ;; err
          local.get $ret
          i32.const 4
          i32.add
          local.get $value
          i32.store)
        (else
          local.get $ret
          i32.const 0
          i32.store            ;; ok
          local.get $ret
          i32.const 4
          i32.add
          i32.const 16
          i32.store            ;; Zeiger auf "gerade"
          local.get $ret
          i32.const 8
          i32.add
          i32.const 6
          i32.store))          ;; Länge
      local.get $ret)
  )
  (core instance $instance (instantiate $module))
  (alias core export $instance "memory" (core memory $memory))
  (alias core export $instance "realloc" (core func $realloc))

  (func $echo (param "data" (list u8)) (result (list u8))
    (canon lift (core func $instance "echo") (memory $memory) (realloc $realloc)))
  (func $classify (param "value" u32) (result (result string (error u32)))
    (canon lift (core func $instance "classify") (memory $memory) (realloc $realloc)))

  (export "echo" (func $echo))
  (export "classify" (func $classify)))
