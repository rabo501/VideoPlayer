-- VideoPlayer: mouse events are handled by mpv, avoiding WindowsFormsHost airspace issues.
local mp = require 'mp'
local dragging = false
local previous = nil
local function clamp(v,a,b) return math.max(a,math.min(b,v)) end
local function dims()
 local d=mp.get_property_native('osd-dimensions')
 if not d or not d.w or not d.h then
  local w,h=mp.get_osd_size()
  if not w or not h or w<=0 or h<=0 then return nil end
  d={w=w,h=h}
 end
 d.vw=d.w-(d.ml or 0)-(d.mr or 0)
 d.vh=d.h-(d.mt or 0)-(d.mb or 0)
 if d.vw<=0 or d.vh<=0 then return nil end
 return d
end
local function zoom(delta)
 -- A wheel event is sufficient to request zoom. Missing hover/OSD metadata
 -- must not silently discard it in embedded video outputs.
 local z=mp.get_property_number('video-zoom',0)
 local nz=clamp(z+delta,0,4)
 local d=dims();local m=mp.get_property_native('mouse-pos')
 if d and m and m.hover~=false and type(m.x)=='number' and type(m.y)=='number' then
  local ratio=2^(nz-z)
  local px=mp.get_property_number('video-pan-x',0)
  local py=mp.get_property_number('video-pan-y',0)
  mp.set_property_number('video-pan-x',clamp(px+(m.x-d.w/2)*(1/ratio-1)/d.vw,-4,4))
  mp.set_property_number('video-pan-y',clamp(py+(m.y-d.h/2)*(1/ratio-1)/d.vh,-4,4))
 end
 mp.set_property_number('video-zoom',nz)
end
mp.add_forced_key_binding('WHEEL_UP','vp-zoom-in',function()zoom(0.2)end)
mp.add_forced_key_binding('WHEEL_DOWN','vp-zoom-out',function()zoom(-0.2)end)
mp.add_forced_key_binding('MBTN_LEFT','vp-drag',function(e)
 if e.event=='down' then dragging=true;previous=mp.get_property_native('mouse-pos')
 elseif e.event=='up' then dragging=false;previous=nil end
end,{complex=true})
mp.observe_property('mouse-pos','native',function(_,m)
 if not m or m.hover==false then dragging=false;previous=nil;return end
 if dragging and previous then
  local d=dims()
  if d then
   mp.set_property_number('video-pan-x',clamp(mp.get_property_number('video-pan-x',0)+(m.x-previous.x)/d.vw,-4,4))
   mp.set_property_number('video-pan-y',clamp(mp.get_property_number('video-pan-y',0)+(m.y-previous.y)/d.vh,-4,4))
  end
 end
 previous=m
end)
local keys={SPACE='play',LEFT='previous',RIGHT='next',i='start',o='end',s='screenshot',['Ctrl+o']='open',['Ctrl+O']='open',r='reset'}
for key,action in pairs(keys)do
 local a=action
 mp.add_forced_key_binding(key,'vp-key-'..key,function()mp.commandv('script-message','vp-action',a)end)
end
mp.register_event('file-loaded',function()dragging=false;previous=nil end)
