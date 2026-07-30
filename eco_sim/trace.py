"""Quick diagnostic to see what skip_first_4 does over time."""
import json
import math
from pathlib import Path
import params as P
import sim
import strategy

map_data = json.loads((Path(__file__).parent / "maps" / "northpolarcap.json").read_text())
nest = (map_data["alienNest"]["x"], map_data["alienNest"]["z"])

# Rank patches by dist from Nest
patches_by_dist = sorted(map_data["patches"],
                         key=lambda p: (nest[0]-p["x"])**2 + (nest[1]-p["z"])**2)
print("Patches by distance from Nest:")
for i, p in enumerate(patches_by_dist[:15]):
    d = math.sqrt((nest[0]-p["x"])**2 + (nest[1]-p["z"])**2)
    print(f"  #{i:2d}: ({p['x']:>7.0f},{p['z']:>7.0f}) d={d:>5.0f}m amount={p['initial']}")

print(f"\nChain reach = {P.CHAIN_REACH_M}m, Node hop max = {2*P.CHAIN_REACH_M}m")
print("So single Node-hop from Nest can reach patches up to 400m away.")

# Run skip_first_4 and trace what happens
strategy.reset_caches()
s = sim.new_state(map_data)
print(f"\nRunning skip_first_4...")
for tick_i in range(30):   # 30 ticks = 30 seconds
    strategy.skip_first_4(s)
    sim.tick(s)
    if tick_i in (0, 5, 10, 15, 20, 25, 29):
        nodes = [(st.x, st.z) for st in s.structs if st.kind == "Node"]
        bcs = [(st.x, st.z) for st in s.structs if st.kind == "BC"]
        queued = [(qi.kind, qi.x, qi.z, qi.complete_at) for qi in s.queue]
        print(f"t={s.t:>3.0f}s cash={s.cash:>5} nodes={nodes} bcs={bcs} queue={queued}")

# What did _node_hop_target return at t=0?
strategy.reset_caches()
s2 = sim.new_state(map_data)
target = strategy._node_hop_target(s2)
print(f"\n_node_hop_target at t=0: {target}")
target_bc = strategy._expansion_target_skipping(s2, strategy._reserved_patch_indices(s2, 4))
print(f"_expansion_target_skipping(4) at t=0: {target_bc}")
