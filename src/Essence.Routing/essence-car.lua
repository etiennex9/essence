-- Essence's car profile: OSRM's own car.lua, except that nothing starts or ends on an autoroute.
--
-- A station on a service road beside an autoroute would otherwise snap to the autoroute, which passes closer, and its
-- detour would read 0 although getting there means leaving at an exit. With motorways and their ramps not start points,
-- stations snap to their own road. A driver and a trip's destination still snap anywhere: the API snaps the stations
-- first, then asks for its tables with snapping=any on the points where the stations met their roads.

api_version = 4

local car = require("car")

local function process_way(profile, way, result, relations)
  car.process_way(profile, way, result, relations)

  local highway = way:get_value_by_key("highway")
  if highway == "motorway" or highway == "motorway_link" then
    result.is_startpoint = false
  end
end

return {
  setup = car.setup,
  process_way = process_way,
  process_node = car.process_node,
  process_turn = car.process_turn,
}
