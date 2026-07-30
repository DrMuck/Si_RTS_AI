"""
Game constants for the eco simulator. Numbers come from live observation and the
mod's hardcoded values in EcoSimulator.cs / ShrimpRelocator.cs. When the game
gets a balance patch these need re-verification (see followup #6.10 in the
project memory: "read constants from game data").
"""

# ---- Structures ----
BC_COST     = 500
BC_BUILD_S  = 20
CYST_COST   = 1500
CYST_BUILD_S = 10
NODE_COST   = 100
NODE_BUILD_S = 5

# ---- Shrimps ----
SHRIMP_COST      = 75     # rough — game's actual value varies with tech tier
SHRIMP_BUILD_S   = 5      # per shrimp from a Cyst / Nest
SHRIMP_SPEED     = 9.0    # m/s (from mod ShrimpRelocator SHRIMP_SPEED constant)
SHRIMP_CARRY     = 400    # per-trip capacity
HARVEST_RATE     = 9.5    # units of biotics / s while at patch
DEPOSIT_RATE     = 50.0   # units of cash / s while at BC/Cyst

# ---- Chains ----
CHAIN_REACH_M    = 200    # BC/Cyst/Node → structure max distance
HARVEST_RANGE_M  = 30     # BC → nearest patch (rough)

# ---- User rules ----
PER_BC_SHRIMP_CAP   = 15
TEAM_SHRIMP_CAP     = 200
HANDOFF_CYST_MIN    = 4000    # skip Cyst pairing below this handoff score
HANDOFF_CYST_FULL   = 8000    # full cluster bonus at/above

# ---- Team economy ----
# Real game gives Alien enough starter cash to place ~2 BCs + starter Cyst
# roughly immediately. Rough numbers from live /state at round start.
START_CASH       = 1500
START_CAP        = 4000
BC_CAP_ADD       = 2000   # rough — each BC adds capacity

# ---- Starter units ----
# Alien Nest ships with some shrimps at round start (the "starter phase 1
# shrimps"). Number varies; 6 is a reasonable middle for NorthPolarCap.
START_NEST_SHRIMPS = 6

# ---- Simulation horizon ----
DEFAULT_HORIZON_S = 600   # 10 minutes for the initial optimization pass
