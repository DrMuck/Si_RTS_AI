/**
 * Si_RTS_AI — Structurizr DSL model
 *
 * A living C4 model of the mod. Edit this file, save, then refresh
 * the Structurizr Lite tab (localhost:8080) to see updated diagrams.
 *
 * C4 levels used here:
 *   L1  System Context   — mod vs. game vs. tooling
 *   L2  Containers       — Perception / Planning / Execution / TestHarness / Commands
 *   L3  Components       — per-container zoom (Planning, Execution shown)
 *
 * Convention: capitalize component names to match their file/class name
 * (e.g. EcoPlanner, not ecoPlanner). The DSL identifier is lowercase.
 */
workspace "Si_RTS_AI" "Silica RTS AI commander mod — subsystem model" {

    model {

        // -----------------------------------------------------------
        // Actors + external systems
        // -----------------------------------------------------------
        commander = person "Human commander"        "Optional. Chee-style human on Alien team; competes with our AI for the same role."
        watcher   = person "Mod author / observer"  "You. Reads LayersViewer + round logs."

        silicaGame = softwareSystem "Silica RTS Game"    "Bohemia Interactive RTS. Hosts MP_Strategy, ResourceArea, AI commander loop, structure/unit lifecycle."   "External"
        melonLoader = softwareSystem "MelonLoader"        "Mod host + Harmony patching + MelonPreferences persistence."                                             "External"
        adminMod   = softwareSystem "SilicaAdminMod"     "Provides PlayerMethods.RegisterPlayerCommand — used for /rtsai chat commands."                           "External"
        unitBalance = softwareSystem "Si_UnitBalance"     "Optional peer mod that mutates ConstructionData at runtime (BC cost, radii, etc.). We honor its edits."  "External"
        layersView = softwareSystem "RTSA LayersViewer"  "Local Python/HTML viewer that pulls heatmaps from TelemetryServer on port 8765."                         "External"

        // -----------------------------------------------------------
        // The mod itself
        // -----------------------------------------------------------
        rtsai = softwareSystem "Si_RTS_AI" "Alien commander AI, plus observability + test harness." {

            // ==== Perception ====
            perception = container "Perception" "Samplers, layer computation, HTTP telemetry" "C# / Il2Cpp reflection" {
                bcMetrics        = component "BcMetrics"          "30s per-BC JSON dump for offline BC-utility regression"
                ecoRateSampler   = component "EcoRateSampler"     "2s cadence. Rolling income, checkpoint benchmarks, optional AutoDrain."
                shrimpSampler    = component "ShrimpStateSampler" "1s per-worker stored/max via reflection."
                fpsSampler       = component "FpsSampler"         "1s CSV — smoothed FPS, peak dt, frames-in-window."
                constDumper      = component "GameConstantsDumper" "One-shot reflect of every field on Shrimp/BC/Cyst/etc."
                telemetryServer  = component "TelemetryServer"    "HttpListener on :8765 — /catalog /layer/... /state /entities /patches."
                gridWorld        = component "GridWorld"          "40m-cell grid, per-map extent lookup."
                alienEcoLayers   = component "AlienEcoLayers"     "BioticsWeighted → CystPressure (BC-masked, existing-Cyst-subtracted)."
                humanEcoLayers   = component "HumanEcoLayers"     "Balterium + HQ masks + EcoHqExpansionValue = weighted × dist-to-HQ."
                fowLayers        = component "FoWLayers"          "Per-team active + explored grids."
                layerReplay      = component "LayerReplay"        "5s snapshots to <round>/<team>/<layer>/*.bin, cache for HTTP server."
            }

            // ==== Planning ====
            planning = container "Planning" "Cash arbitration, beam-search eco planner, tech + relocator" "C# — async on background Task" {
                moneyBroker      = component "MoneyBroker"        "IActionSource coordinator. Score × (1+urgency), fires under cash − reservations."
                ecoPlanner       = component "EcoPlanner"         "Async beam search (depth 5, width 6, horizon 180s). Phase 1 base eco → Phase 2 expand."
                ecoPlannerConfig = component "EcoPlannerConfig"   "Runtime knobs: cluster radius, uncysted BC queue cap, target-farthest flag."
                techPlanner      = component "TechPlanner"        "Cortex placement + Alpha..Omega research queue. Reserves 2000 hard."
                cashFlowProjector = component "CashFlowProjector"  "Producer-count × measured IPS + shrimp-ramp integral. (Not yet consumed by TechPlanner.)"
                shrimpRelocator  = component "ShrimpRelocator"    "Per-shrimp marginal-income migration with life-critical discounts."
                ecoState         = component "EcoState / Builder / Simulator" "Cloneable snapshot + deterministic 2s forward stepping with crowd(N) factor."
                compositionPlan  = component "CompositionPlanner" "Static Priority lists per phase × faction — MVP placeholder."
            }

            // ==== Execution / Faction ====
            faction = container "Execution / Faction" "Harmony patches into Silica's AI handler + per-faction owners" "Harmony prefixes/postfixes" {
                factionControl   = component "FactionControl"           "Per-faction master switch — team-name substring match."
                alienConstr      = component "AlienConstruction"        "Prefix on AIConstructionHandler.Think (Alien). Owns tick when planner active."
                shrimpProducer   = component "AlienShrimpProducer"      "Per-Cyst Construct(shrimpCd) with per-BC + team caps."
                shrimpAntiAttack = component "AlienShrimpAntiAttack"    "Strips shrimps from attack groups, blocks non-harvest moves."
                militaryManager  = component "MilitaryManager"          "Threat scan, target ranking, unit production. Army orders STUBBED."
                humanConstr      = component "HumanConstruction"        "Sol/Centauri refinery + HQ + tech (single Barracks/Research cap)."
                humanHarvester   = component "HumanHarvesterController" "Postfix routing rules (init/anti-clump/queue/stuck)."
                humanTech        = component "HumanTechResearcher"      "Mark I..V queue at each Research Facility."
                suppressCombat   = component "SuppressCombat"           "Global attack-order suppress (test aid)."
                suppressHumanAI  = component "SuppressHumanAI"          "Suicide human units + zero cash (EnemyBroke mode)."
            }

            // ==== Test harness + commands + suppression ====
            testHarness = container "TestHarness" "Headless auto-start / force-end / prefs / boot HTTP server" "MelonPreferences + Harmony" {
                harness          = component "TestHarness"        "Reads all prefs, auto-starts round, force-ends after N min. IsRoundActive published every tick."
                startTelemetry   = component "TelemetryServer boot" "Starts HttpListener whenever TelemetryPort > 0. Decoupled from TestMode."
            }

            commands = container "Commands" "In-game chat commands + Phase 3.1 unit override" "SilicaAdminMod" {
                chatCmds         = component "Commands (/rtsai)"  "status | override | enable — admin-gated via Power.Generic."
                phase31          = component "Phase31_Production" "Postfix on AIUnitHandler.GetBestUnitToBuildForPreset — swaps unit pick to CompositionPlanner target."
            }
        }

        // -----------------------------------------------------------
        // Relationships — data + control flow that actually exists
        // -----------------------------------------------------------

        // Actors ↔ system
        commander -> silicaGame  "Plays as Alien Commander (optional)"
        watcher   -> layersView  "Watches heatmaps"
        watcher   -> rtsai       "Edits config, reads logs"

        // Mod loads inside its host
        melonLoader -> rtsai      "Hosts + Harmony-patches"
        rtsai -> silicaGame       "Prefixes/postfixes AI handlers, reads Team/Structure/ResourceArea"
        rtsai -> adminMod         "Registers /rtsai commands"
        rtsai -> unitBalance      "Reads CDs mutated by peer mod"

        // Telemetry out
        telemetryServer -> layersView "HTTP :8765 — layer bytes + state"

        // Perception → Planning
        ecoRateSampler   -> cashFlowProjector "Measured income per shrimp"
        cashFlowProjector -> techPlanner        "Reservation math (not yet used)"
        fowLayers        -> ecoPlanner         "Explored mask (main-thread snapshot)"
        alienEcoLayers   -> alienConstr        "CystPressure argmax for Phase B"
        humanEcoLayers   -> humanConstr        "EcoHqExpansionValue argmax"
        gridWorld        -> alienEcoLayers     "Cell coords / extent"
        gridWorld        -> humanEcoLayers     "Cell coords / extent"
        gridWorld        -> fowLayers          "Cell coords / extent"
        layerReplay      -> telemetryServer    "Latest-bytes cache (per team/layer)"

        // Planning coordination
        moneyBroker -> techPlanner  "Sorts + fires proposals"
        techPlanner -> moneyBroker  "PublishState + reservations"
        ecoPlanner  -> moneyBroker  "Reads GetReservedCash (subtracts from budget)"

        // Planning → Execution
        ecoPlanner  -> alienConstr  "TryBuildStructureForPlanner(BC / Cyst / Node)"
        techPlanner -> alienConstr  "TryBuildStructureByCd(Cortex + research)"
        ecoPlanner  -> shrimpProducer "Also ticks producer at end of plan"
        ecoPlanner  -> shrimpRelocator "MaybeRun after apply"
        militaryManager -> alienConstr "Higher-tier producer placement (Greater/Colossus/Grand)"

        // Execution ↔ game
        alienConstr      -> silicaGame  "Places structures via QueueFirstValidPlacementAroundPoint"
        shrimpProducer   -> silicaGame  "Cyst.Construct(shrimpCd)"
        shrimpAntiAttack -> silicaGame  "Filters AIGroup.OnAttackOrder, Unit.OnMoveOrder"
        militaryManager  -> silicaGame  "Produces combat units, TODO: orders army"
        humanConstr      -> silicaGame  "Refinery / HQ / Barracks / Research placement"
        humanHarvester   -> silicaGame  "Postfix harvester routing"
        humanTech        -> silicaGame  "Structure.Construct(markN cd)"
        factionControl   -> alienConstr    "Master switch"
        factionControl   -> humanConstr    "Master switch"

        // Test harness
        harness          -> factionControl    "Push RTSAI_* prefs"
        harness          -> suppressHumanAI   "EnemyBroke tick"
        harness          -> suppressCombat    "SuppressCombat pref"
        harness          -> ecoRateSampler    "AutoDrain toggle"
        harness          -> militaryManager   "MilitaryEnabled + tunables"
        harness          -> startTelemetry    "TelemetryPort → server start"
        startTelemetry   -> telemetryServer   "HttpListener boot"

        // Commands
        chatCmds -> factionControl "enable <faction> on|off"
        chatCmds -> phase31        "override [team] on|off"
        phase31  -> compositionPlan "Asks target for TeamState"
        phase31  -> silicaGame     "Replaces AIUnitHandler pick"
    }

    views {

        // L1 — the "why does this mod exist" picture
        systemContext rtsai "L1_Context" {
            include *
            autolayout lr
            description "Where the mod sits — between MelonLoader (host) and the Silica game, feeding the local LayersViewer."
        }

        // L2 — subsystem map. This is the diagram you'll look at most.
        container rtsai "L2_Containers" {
            include *
            autolayout tb
            description "Perception / Planning / Execution / Test / Commands — the 5 in-mod containers."
        }

        // L3 — Planning internals. The heart of the mod.
        component planning "L3_Planning" {
            include *
            autolayout tb
            description "EcoPlanner + TechPlanner + MoneyBroker + supporting state. MoneyBroker is currently only wired to TechPlanner."
        }

        // L3 — Execution internals. Who actually places things.
        component faction "L3_Execution" {
            include *
            autolayout tb
            description "Harmony-patched handlers per faction. MilitaryManager army orders are stubbed."
        }

        // L3 — Perception internals.
        component perception "L3_Perception" {
            include *
            autolayout tb
            description "Samplers + MapLayers DAG + TelemetryServer. Nothing here mutates game state."
        }

        // Styling — communicate status at a glance.
        styles {
            element "Software System" {
                background #131A15
                color      #DDE4DE
                stroke     #88C070
            }
            element "External" {
                background #1A221C
                color      #9AA69E
                stroke     #6E7972
                shape      RoundedBox
            }
            element "Person" {
                background #88C070
                color      #0E1310
                shape      Person
            }
            element "Container" {
                background #171E19
                color      #DDE4DE
                stroke     #88C070
            }
            element "Component" {
                background #1F2822
                color      #DDE4DE
                stroke     #88C070
            }
        }

        theme default
    }
}
