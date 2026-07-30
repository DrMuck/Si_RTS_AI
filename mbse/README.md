# Si_RTS_AI — MBSE workspace

A text-based, git-versioned architecture model for the mod, using the
[Structurizr DSL](https://docs.structurizr.com/dsl) (C4 method).

## Files

| File | Purpose |
|------|---------|
| `workspace.dsl` | The model itself — actors, containers, components, relationships, views. |
| `README.md` | This file. |

Nothing else lives here. Structurizr Lite writes a `.structurizr/` cache
next to the DSL on first run; add it to `.gitignore` if you want.

## Run the viewer

Requires Java 17+ (Temurin 21 recommended: `winget install EclipseAdoptium.Temurin.21.JDK`).
The WAR lives alongside this README (`structurizr-lite.war`, ~167 MB, git-ignored).

```powershell
cd C:\Users\schwe\Projects\Si_RTS_AI\mbse
java -jar structurizr-lite.war .
```

Then open http://localhost:8080. Edit `workspace.dsl` in VS Code, save,
refresh the browser tab — diagrams re-render live. `Ctrl+C` in the PowerShell
window to stop.

*(Prefer Docker? `docker run -it --rm -p 8080:8080 -v C:\Users\schwe\Projects\Si_RTS_AI\mbse:/usr/local/structurizr structurizr/lite` — same result, needs Docker Desktop installed.)*

## Views defined

| Level | View key | What it shows |
|-------|----------|---------------|
| L1 | `L1_Context`      | The mod in its ecosystem: MelonLoader, Silica game, LayersViewer, admin mod. |
| L2 | `L2_Containers`   | The five in-mod containers: Perception / Planning / Execution / TestHarness / Commands. |
| L3 | `L3_Planning`     | Zoom on the planning stack: EcoPlanner + TechPlanner + MoneyBroker + support. |
| L3 | `L3_Execution`    | Zoom on faction handlers + military manager. |
| L3 | `L3_Perception`   | Zoom on samplers + MapLayers DAG + telemetry server. |

## Convention

- **Nouns from code**: Component names match the C# class name where possible
  (`EcoPlanner`, `MoneyBroker`) so grep and the diagram agree.
- **Relationships are real**: An arrow means data or control actually flows
  in that direction in the current code, not what we plan to build. Aspirational
  edges (e.g. EcoPlanner → MoneyBroker as an IActionSource) are omitted until
  they exist.
- **Status via description**: Where a component is stubbed or partial, its
  description says so (e.g. "Army orders STUBBED", "Not yet consumed by
  TechPlanner"). Keep this honest — the model is only useful if it matches
  reality.

## Maintenance loop

When you touch a subsystem:

1. Change the code.
2. Open `workspace.dsl`, adjust the affected component / relationship / description.
3. Save. Refresh Structurizr Lite. Verify the diagram still tells the truth.

When you can't figure out how to update the model, that's a modeling lesson —
the code probably grew a coupling that isn't in the model's vocabulary yet.

## Next steps (learning path)

Once you're comfortable with C4:

- Add **deployment views** (Silica dedicated server host + mod DLL + viewer client).
- Add **dynamic views** (sequence of Round_Start → first BC → first Cortex research).
- Attach **decision records** — Structurizr supports embedded ADRs.
- If you want to jump to real SysML, translate this workspace to PlantUML SysML
  syntax as an exercise — the same model, different notation, and you'll feel
  where the two frameworks disagree.
