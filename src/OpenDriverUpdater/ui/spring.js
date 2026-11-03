// Muelles para las animaciones (mismos parametros que SwiftUI: response + dampingFraction).
// dampingFraction 1 = sin rebote, menos de 1 = rebota un poco.
// Si se cambia el destino a mitad de camino se mantiene la velocidad, asi nunca pega saltos.
(function () {
  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

  class Spring {
    constructor({ value = 0, response = 0.35, dampingFraction = 1, precision = 0.001, onUpdate, onRest } = {}) {
      this.value = value;
      this.target = value;
      this.velocity = 0;
      this.precision = precision;
      this.onUpdate = onUpdate;
      this.onRest = onRest;
      this._raf = 0;
      this._last = 0;
      this.configure(response, dampingFraction);
      this._tick = this._tick.bind(this);
    }

    configure(response, dampingFraction) {
      this.stiffness = Math.pow((2 * Math.PI) / response, 2);
      this.damping = (4 * Math.PI * dampingFraction) / response;
      return this;
    }

    get isAnimating() { return this._raf !== 0; }

    // velocity (opcional) en unidades/s, p.ej. la del raton al soltar un arrastre
    to(target, velocity) {
      this.target = target;
      if (velocity !== undefined) this.velocity = velocity;
      if (reduceMotion.matches) return this.jump(target);
      if (!this._raf) {
        this._last = performance.now();
        this._raf = requestAnimationFrame(this._tick);
      }
      return this;
    }

    // sin animar
    jump(value) {
      if (this._raf) cancelAnimationFrame(this._raf);
      this._raf = 0;
      this.value = this.target = value;
      this.velocity = 0;
      this.onUpdate && this.onUpdate(value);
      this.onRest && this.onRest(value);
      return this;
    }

    // mientras se arrastra: sigue al raton sin fisica
    track(value) {
      if (this._raf) cancelAnimationFrame(this._raf);
      this._raf = 0;
      this.value = this.target = value;
      this.onUpdate && this.onUpdate(value);
    }

    _tick(now) {
      let dt = Math.min((now - this._last) / 1000, 1 / 20);
      this._last = now;
      // pasos fijos de 1/240 s para que vaya igual a 60 que a 144 Hz
      const h = 1 / 240;
      while (dt > 1e-6) {
        const step = Math.min(h, dt);
        const force = -this.stiffness * (this.value - this.target) - this.damping * this.velocity;
        this.velocity += force * step;
        this.value += this.velocity * step;
        dt -= step;
      }
      const settled = Math.abs(this.velocity) < this.precision * 10 && Math.abs(this.value - this.target) < this.precision;
      if (settled) {
        this.value = this.target;
        this.velocity = 0;
        this._raf = 0;
        this.onUpdate && this.onUpdate(this.value);
        this.onRest && this.onRest(this.value);
        return;
      }
      this.onUpdate && this.onUpdate(this.value);
      this._raf = requestAnimationFrame(this._tick);
    }
  }

  // Simula el muelle y lo convierte en una curva CSS linear(), para las transiciones
  // simples (botones al soltar, switch...). Las transiciones CSS tambien se interrumpen bien.
  function springEasing(response, dampingFraction) {
    const k = Math.pow((2 * Math.PI) / response, 2);
    const c = (4 * Math.PI * dampingFraction) / response;
    let x = 0, v = 0, t = 0;
    const h = 1 / 600, samples = [];
    while (t < 3) {
      for (let i = 0; i < 10; i++) { v += (-k * (x - 1) - c * v) * h; x += v * h; t += h; }
      samples.push(x);
      if (Math.abs(x - 1) < 0.0005 && Math.abs(v) < 0.01) break;
    }
    samples[samples.length - 1] = 1;
    const step = Math.max(1, Math.floor(samples.length / 48));
    const points = [0];
    for (let i = step; i < samples.length; i += step) points.push(+samples[i].toFixed(4));
    if (points[points.length - 1] !== 1) points.push(1);
    return { easing: `linear(${points.join(', ')})`, duration: Math.round(t * 1000) };
  }

  // resistencia tipo iOS: cuanto mas estiras menos se mueve
  function rubberBand(offset, dimension, constant = 0.55) {
    const sign = Math.sign(offset);
    const x = Math.abs(offset);
    return sign * (1 - 1 / ((x * constant) / dimension + 1)) * dimension;
  }

  // velocidad del gesto (px/s) con los ultimos ~100 ms
  class VelocityTracker {
    constructor() { this.samples = []; }
    reset() { this.samples = []; }
    add(value) {
      const t = performance.now();
      this.samples.push({ t, value });
      while (this.samples.length > 2 && t - this.samples[0].t > 100) this.samples.shift();
    }
    get velocity() {
      if (this.samples.length < 2) return 0;
      const a = this.samples[0], b = this.samples[this.samples.length - 1];
      const dt = (b.t - a.t) / 1000;
      return dt > 0 ? (b.value - a.value) / dt : 0;
    }
  }

  window.Motion = { Spring, springEasing, rubberBand, VelocityTracker, reduceMotion };
})();
