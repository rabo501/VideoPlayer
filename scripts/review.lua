-- VideoPlayer 1.2.1. Windows Raw Input provides pointer gestures over embedded mpv.
-- Only this handler changes zoom/pan: no duplicate native wheel or drag bindings.
local mp = require 'mp'
local function finite(x) return type(x)=='number' and x==x and x~=math.huge and x~=-math.huge end
local function clamp(v,a,b)return math.max(a,math.min(b,v))end
local function dimensions(w,h,z)
 local d=mp.get_property_native('osd-dimensions')
 if d and finite(d.w) and finite(d.h) and d.w>0 and d.h>0 then
  local vw=d.w-(d.ml or 0)-(d.mr or 0)
  local vh=d.h-(d.mt or 0)-(d.mb or 0)
  if vw>0 and vh>0 then return d.w,d.h,vw,vh end
 end
 local v=mp.get_property_native('video-out-params') or mp.get_property_native('video-params')
 if not v or not finite(v.dw) or not finite(v.dh) or v.dw<=0 or v.dh<=0 then return nil end
 local dw,dh=v.dw,v.dh
 local rotation=mp.get_property_number('video-rotate',0)+(v.rotate or 0)
 if rotation%180~=0 then dw,dh=dh,dw end
 local fit=math.min(w/dw,h/dh);local fill=math.max(w/dw,h/dh)
 local scale=(fit+(fill-fit)*clamp(mp.get_property_number('panscan',0),0,1))*2^z
 return w,h,dw*scale,dh*scale
end
mp.register_script_message('vp-pointer',function(sdx,sdy,swheel,sx,sy,sw,sh)
 local dx,dy,wheel,x,y,w,h=tonumber(sdx),tonumber(sdy),tonumber(swheel),tonumber(sx),tonumber(sy),tonumber(sw),tonumber(sh)
 if not (finite(dx) and finite(dy) and finite(wheel) and finite(x) and finite(y) and finite(w) and finite(h)) or w<=0 or h<=0 then return end
 local z=mp.get_property_number('video-zoom',0)
 local nz=clamp(z+wheel*0.2,0,4)
 local px=mp.get_property_number('video-pan-x',0);local py=mp.get_property_number('video-pan-y',0)
 local ow,oh,vw,vh=dimensions(w,h,z)
 if ow then
  px=px+dx*ow/vw;py=py+dy*oh/vh
  local ratio=2^(nz-z)
  px=px+(x-0.5)*ow/vw*(1/ratio-1)
  py=py+(y-0.5)*oh/vh*(1/ratio-1)
 end
 if nz==0 and mp.get_property_number('panscan',0)==0 then px=0;py=0 end
 mp.set_property_number('video-zoom',nz)
 mp.set_property_number('video-pan-x',clamp(px,-4,4))
 mp.set_property_number('video-pan-y',clamp(py,-4,4))
end)
local keys={SPACE='play',LEFT='previous',RIGHT='next',i='start',o='end',s='screenshot',['Ctrl+o']='open',['Ctrl+O']='open',r='reset'}
for key,action in pairs(keys)do
 local a=action
 mp.add_forced_key_binding(key,'vp-key-'..key,function()mp.commandv('script-message','vp-action',a)end)
end
mp.add_forced_key_binding('MBTN_RIGHT','vp-context',function()mp.commandv('script-message','vp-action','context')end)
