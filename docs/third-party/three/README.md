# Three.js

Pinned graphics runtime: `three@0.186.1`, MIT, copyright2010–2026 three.js authors. The unmodified [LICENSE](LICENSE) accompanies the runtime in local packages. Source: [upstream](https://github.com/mrdoob/three.js), [exact npm archive](https://registry.npmjs.org/three/-/three-0.186.1.tgz); archive integrity is pinned in the root package lock.

Three.js supplies graphics primitives and WebGL rendering. VibeMiner owns acquisition, WoW decoding, VMG validation, geometry interpretation and camera policy. No Three asset loader, physics engine, mesh optimizer or external model parser is a game-data path.

Development-only `@types/three@0.186.0` supplies TypeScript declarations under the accompanying [TYPES-LICENSE](TYPES-LICENSE). Its transitive compiler dependencies remain development-only; the graphics runtime itself has no npm dependencies. Existing dependency versions were preserved when adding these pins.

The [renderer documentation](https://threejs.org/docs/pages/WebGLRenderer.html) and [BufferAttribute documentation](https://threejs.org/docs/pages/BufferAttribute.html) were consulted for WebGL2, disposal and direct typed-array attributes. Adding this dependency does not expose or qualify the diagnostic scene; actual rendering, ownership, resources and native captures have separate acceptance.
