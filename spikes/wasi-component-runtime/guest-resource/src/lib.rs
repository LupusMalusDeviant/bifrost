//! Guest-Component mit einer WIT-Resource. Der Zustand hängt am Handle, nicht am Aufruf — genau
//! deshalb braucht der Host dafür eine persistente Instanz.
//!
//! Das WIT kommt aus `docs/spikes/fixtures/counter.wit`, damit Guest und Host-Test denselben
//! Vertrag lesen statt zwei Kopien, die auseinanderlaufen können.

wit_bindgen::generate!({
    path: "../../../docs/spikes/fixtures/counter.wit",
    world: "counter-host",
});

use std::cell::Cell;

use exports::mcpmcp::counter::counters::{Guest, GuestCounter};

struct Component;

struct Counter {
    value: Cell<i32>,
}

impl GuestCounter for Counter {
    fn new(start: i32) -> Self {
        Self {
            value: Cell::new(start),
        }
    }

    fn bump(&self, by: i32) -> i32 {
        self.value.set(self.value.get() + by);
        self.value.get()
    }

    fn value(&self) -> i32 {
        self.value.get()
    }
}

impl Guest for Component {
    type Counter = Counter;

    fn double_of(which: exports::mcpmcp::counter::counters::CounterBorrow<'_>) -> i32 {
        which.get::<Counter>().value.get() * 2
    }
}

export!(Component);
