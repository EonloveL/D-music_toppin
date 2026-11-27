using System.Text;

namespace MusicToppin;

/// <summary>
/// 开发分支原型：手写 SVG 分层宠物（暹罗猫 / 腊肠狗 / 德文猫）+ 全套动作，用于验证交互。
/// 访问 /pets 使用；?demo=1 显示动作按钮；?pet=siamese|dachshund|devon 选择形象；透明背景，可直接当浏览器源。
/// </summary>
internal static class SvgPet
{
    public const string Page = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<title>Music Toppin · SVG Pet</title>
<style>
  html,body{margin:0;padding:0;width:100%;height:100%;background:transparent;overflow:hidden;font-family:'Microsoft YaHei UI',sans-serif;}
  #pet{position:absolute;left:50%;bottom:6%;width:74%;height:74%;transform:translateX(-50%);transform-origin:50% 88%;}
  .rig{display:none;}
  .rig.on{display:block;}
  #pet.idle .rig.on .body{animation:breathe 3.6s ease-in-out infinite;}
  #pet.idle .rig.on .tail{animation:wag 2.8s ease-in-out infinite;}
  #pet.hop{animation:hop .45s cubic-bezier(.3,1.6,.4,1);}
  #pet.shake{animation:shake .45s ease-in-out;}
  #pet.pounce{animation:pounce .5s cubic-bezier(.2,1.5,.4,1);}
  #pet.dead{animation:dead .8s ease forwards;}
  #pet.tilt .rig.on .head{transform:rotate(-9deg);}
  #pet.sing .rig.on .mouth{animation:sing .35s ease-in-out 3;}
  #pet.glow{filter:drop-shadow(0 0 10px rgba(143,184,255,.85));}
  #pet .rig .head{transition:transform .35s ease;}
  #pet .rig .pawR{transition:transform .25s ease;}
  #pet.wave .rig.on .pawR{animation:wave .5s ease-in-out 3;}
  .blink .rig.on .eyeOpen{visibility:hidden;}
  .blink .rig.on .eyeClosed{visibility:visible;}
  .rig .eyeClosed{visibility:hidden;}
  @keyframes breathe{0%,100%{transform:scale(1,1)}50%{transform:scale(1.015,1.025)}}
  @keyframes wag{0%,100%{transform:rotate(0deg)}50%{transform:rotate(-16deg)}}
  @keyframes hop{0%,100%{transform:translate(-50%,0)}40%{transform:translate(-50%,-26px) scale(1.02)}}
  @keyframes shake{0%,100%{transform:translate(-50%,0)}20%{transform:translate(-54%,0) rotate(-3deg)}60%{transform:translate(-46%,0) rotate(3deg)}}
  @keyframes pounce{0%{transform:translate(-50%,0) scale(1)}35%{transform:translate(-50%,-30px) scale(1.06) rotate(-6deg)}70%{transform:translate(-50%,6px) scale(.98)}100%{transform:translate(-50%,0)}}
  @keyframes dead{to{transform:translate(-50%,18px) rotate(78deg);opacity:.85}}
  @keyframes wave{0%,100%{transform:rotate(0)}40%{transform:rotate(-38deg)}}
  @keyframes sing{0%,100%{transform:scaleY(.55)}50%{transform:scaleY(1.7)}}
  #sign{position:absolute;left:50%;bottom:70%;transform:translateX(-50%);background:rgba(14,17,23,.92);border:1px solid rgba(255,255,255,.18);
    border-radius:12px;padding:8px 14px;color:#eaf2ff;font-size:16px;white-space:nowrap;opacity:0;transition:opacity .2s;pointer-events:none;max-width:88%;overflow:hidden;text-overflow:ellipsis;}
  #sign.on{opacity:1;}
  #fx{position:absolute;inset:0;pointer-events:none;overflow:hidden;}
  .heart,.star,.confetti{position:absolute;font-size:22px;animation:floatUp 1.6s ease-out forwards;}
  @keyframes floatUp{0%{transform:translateY(0) scale(.7);opacity:0}20%{opacity:1}100%{transform:translateY(-150px) scale(1.1);opacity:0}}
  #crown{position:absolute;left:50%;top:4%;transform:translateX(-50%);font-size:34px;opacity:0;transition:opacity .2s;}
  #crown.on{opacity:1;}
  #demo{position:absolute;left:8px;top:8px;display:flex;flex-wrap:wrap;gap:6px;max-width:92%;}
  #demo button{font-size:12px;padding:4px 8px;border-radius:6px;border:1px solid rgba(255,255,255,.25);background:rgba(14,17,23,.8);color:#eaf2ff;cursor:pointer;}
  #demo .sep{width:100%;height:0;}
  .hidden{display:none!important;}
</style>
</head>
<body>
<svg id=""pet"" viewBox=""0 0 200 200"" class=""idle"" xmlns=""http://www.w3.org/2000/svg"">
  <defs>
    <linearGradient id=""furSiamese"" x1=""0"" y1=""0"" x2=""0"" y2=""1""><stop offset=""0"" stop-color=""#f6ead6""/><stop offset=""1"" stop-color=""#e6cdaa""/></linearGradient>
    <linearGradient id=""furDach"" x1=""0"" y1=""0"" x2=""0"" y2=""1""><stop offset=""0"" stop-color=""#c98a4b""/><stop offset=""1"" stop-color=""#a4662c""/></linearGradient>
    <linearGradient id=""furDevon"" x1=""0"" y1=""0"" x2=""0"" y2=""1""><stop offset=""0"" stop-color=""#e2d6c4""/><stop offset=""1"" stop-color=""#c7b49a""/></linearGradient>
    <radialGradient id=""cheek"" cx=""0.5"" cy=""0.5"" r=""0.5""><stop offset=""0"" stop-color=""#ffd9c2"" stop-opacity=""0.9""/><stop offset=""1"" stop-color=""#ffd9c2"" stop-opacity=""0""/></radialGradient>
  </defs>

  <!-- 暹罗猫 -->
  <g class=""rig"" id=""rig-siamese"">
    <ellipse cx=""100"" cy=""190"" rx=""56"" ry=""11"" fill=""rgba(0,0,0,.20)""/>
    <g class=""tail"" style=""transform-origin:48px 158px"">
      <path d=""M48 158 C20 150 12 120 26 104 C34 95 48 98 46 110 C44 120 34 124 34 134 C34 146 44 150 52 152 Z"" fill=""#6b5140"" stroke=""#4e3a2d"" stroke-width=""2""/>
    </g>
    <g class=""body"" style=""transform-origin:100px 182px"">
      <ellipse cx=""100"" cy=""152"" rx=""52"" ry=""36"" fill=""url(#furSiamese)"" stroke=""#c9a87c"" stroke-width=""2""/>
      <ellipse cx=""100"" cy=""161"" rx=""32"" ry=""22"" fill=""#fff8ee"" opacity=""0.9""/>
    </g>
    <g class=""pawL""><ellipse cx=""66"" cy=""178"" rx=""14"" ry=""9"" fill=""#6b5140"" stroke=""#4e3a2d"" stroke-width=""2""/></g>
    <g class=""pawR"" style=""transform-origin:136px 178px""><ellipse cx=""134"" cy=""178"" rx=""14"" ry=""9"" fill=""#6b5140"" stroke=""#4e3a2d"" stroke-width=""2""/></g>
    <g class=""head"" style=""transform-origin:100px 96px"">
      <polygon points=""66,62 58,20 92,46"" fill=""#6b5140"" stroke=""#4e3a2d"" stroke-width=""2""/>
      <polygon points=""134,62 142,20 108,46"" fill=""#6b5140"" stroke=""#4e3a2d"" stroke-width=""2""/>
      <circle cx=""100"" cy=""94"" r=""42"" fill=""url(#furSiamese)"" stroke=""#c9a87c"" stroke-width=""2""/>
      <ellipse cx=""100"" cy=""104"" rx=""26"" ry=""19"" fill=""#8a6b52"" opacity=""0.55""/>
      <circle cx=""72"" cy=""104"" r=""11"" fill=""url(#cheek)""/><circle cx=""128"" cy=""104"" r=""11"" fill=""url(#cheek)""/>
      <ellipse class=""eyeOpen"" cx=""84"" cy=""88"" rx=""6.5"" ry=""8"" fill=""#4a90d9""/>
      <ellipse class=""eyeOpen"" cx=""116"" cy=""88"" rx=""6.5"" ry=""8"" fill=""#4a90d9""/>
      <ellipse cx=""86"" cy=""85"" rx=""2"" ry=""2.6"" fill=""#fff""/><ellipse cx=""118"" cy=""85"" rx=""2"" ry=""2.6"" fill=""#fff""/>
      <path class=""eyeClosed"" d=""M77 89 Q84 95 91 89"" stroke=""#4e3a2d"" stroke-width=""3"" fill=""none"" stroke-linecap=""round""/>
      <path class=""eyeClosed"" d=""M109 89 Q116 95 123 89"" stroke=""#4e3a2d"" stroke-width=""3"" fill=""none"" stroke-linecap=""round""/>
      <path d=""M96 102 L104 102 L100 107 Z"" fill=""#e08a8a""/>
      <g class=""mouth"" style=""transform-origin:100px 110px"">
        <path d=""M100 107 Q94 114 88 110"" stroke=""#6b5140"" stroke-width=""2"" fill=""none"" stroke-linecap=""round""/>
        <path d=""M100 107 Q106 114 112 110"" stroke=""#6b5140"" stroke-width=""2"" fill=""none"" stroke-linecap=""round""/>
      </g>
      <path d=""M58 94 L44 90 M58 100 L44 100"" stroke=""#c9a87c"" stroke-width=""1.6"" stroke-linecap=""round""/>
      <path d=""M142 94 L156 90 M142 100 L156 100"" stroke=""#c9a87c"" stroke-width=""1.6"" stroke-linecap=""round""/>
    </g>
  </g>

  <!-- 腊肠狗 -->
  <g class=""rig"" id=""rig-dachshund"">
    <ellipse cx=""100"" cy=""191"" rx=""62"" ry=""10"" fill=""rgba(0,0,0,.20)""/>
    <g class=""tail"" style=""transform-origin:44px 150px"">
      <path d=""M44 150 C20 144 14 120 28 112 C38 106 46 114 42 124 C39 132 34 136 36 142 Z"" fill=""#8a5526"" stroke=""#5f3a17"" stroke-width=""2""/>
    </g>
    <g class=""body"" style=""transform-origin:100px 184px"">
      <ellipse cx=""100"" cy=""156"" rx=""66"" ry=""30"" fill=""url(#furDach)"" stroke=""#7d4a1f"" stroke-width=""2""/>
      <ellipse cx=""102"" cy=""166"" rx=""40"" ry=""16"" fill=""#e8cfa8"" opacity=""0.85""/>
    </g>
    <g class=""pawL""><rect x=""58"" y=""174"" width=""20"" height=""14"" rx=""6"" fill=""#8a5526"" stroke=""#5f3a17"" stroke-width=""2""/></g>
    <g class=""pawR"" style=""transform-origin:142px 178px""><rect x=""122"" y=""174"" width=""20"" height=""14"" rx=""6"" fill=""#8a5526"" stroke=""#5f3a17"" stroke-width=""2""/></g>
    <g class=""head"" style=""transform-origin:100px 96px"">
      <path d=""M64 70 C48 64 42 84 46 102 C50 116 62 118 70 108 Z"" fill=""#8a5526"" stroke=""#5f3a17"" stroke-width=""2""/>
      <path d=""M136 70 C152 64 158 84 154 102 C150 116 138 118 130 108 Z"" fill=""#8a5526"" stroke=""#5f3a17"" stroke-width=""2""/>
      <circle cx=""100"" cy=""92"" r=""36"" fill=""url(#furDach)"" stroke=""#7d4a1f"" stroke-width=""2""/>
      <ellipse cx=""100"" cy=""112"" rx=""22"" ry=""14"" fill=""#c98a4b"" stroke=""#7d4a1f"" stroke-width=""1.5""/>
      <ellipse cx=""100"" cy=""106"" rx=""7"" ry=""5"" fill=""#2e2a26""/>
      <ellipse class=""eyeOpen"" cx=""86"" cy=""86"" rx=""5.5"" ry=""7"" fill=""#3a2a1c""/>
      <ellipse class=""eyeOpen"" cx=""114"" cy=""86"" rx=""5.5"" ry=""7"" fill=""#3a2a1c""/>
      <ellipse cx=""88"" cy=""83"" rx=""1.8"" ry=""2.2"" fill=""#fff""/><ellipse cx=""116"" cy=""83"" rx=""1.8"" ry=""2.2"" fill=""#fff""/>
      <path class=""eyeClosed"" d=""M80 87 Q86 93 92 87"" stroke=""#3a2a1c"" stroke-width=""3"" fill=""none"" stroke-linecap=""round""/>
      <path class=""eyeClosed"" d=""M108 87 Q114 93 120 87"" stroke=""#3a2a1c"" stroke-width=""3"" fill=""none"" stroke-linecap=""round""/>
      <g class=""mouth"" style=""transform-origin:100px 118px"">
        <path d=""M100 111 L100 116"" stroke=""#5f3a17"" stroke-width=""2"" stroke-linecap=""round""/>
        <path d=""M100 116 Q94 121 89 117 M100 116 Q106 121 111 117"" stroke=""#5f3a17"" stroke-width=""2"" fill=""none"" stroke-linecap=""round""/>
      </g>
    </g>
  </g>

  <!-- 德文猫 -->
  <g class=""rig"" id=""rig-devon"">
    <ellipse cx=""100"" cy=""190"" rx=""50"" ry=""10"" fill=""rgba(0,0,0,.20)""/>
    <g class=""tail"" style=""transform-origin:52px 156px"">
      <path d=""M52 156 C24 150 16 118 30 102 C40 90 52 98 48 110 C45 120 36 124 38 136 C40 146 48 148 54 150 Z"" fill=""#c7b49a"" stroke=""#a3907a"" stroke-width=""2""/>
      <path d=""M44 128 q-5 6 1 12 M40 140 q-5 6 1 10"" stroke=""#a3907a"" stroke-width=""1.5"" fill=""none"" stroke-linecap=""round""/>
    </g>
    <g class=""body"" style=""transform-origin:100px 182px"">
      <ellipse cx=""100"" cy=""152"" rx=""46"" ry=""38"" fill=""url(#furDevon)"" stroke=""#b9a58d"" stroke-width=""2""/>
      <path d=""M66 140 q8 5 2 12 M78 158 q8 5 2 12 M112 138 q8 5 2 12 M126 156 q8 5 2 12"" stroke=""#b9a58d"" stroke-width=""1.6"" fill=""none"" stroke-linecap=""round""/>
      <ellipse cx=""100"" cy=""162"" rx=""28"" ry=""20"" fill=""#f1e8db"" opacity=""0.8""/>
    </g>
    <g class=""pawL""><ellipse cx=""70"" cy=""178"" rx=""13"" ry=""8"" fill=""#c7b49a"" stroke=""#a3907a"" stroke-width=""2""/></g>
    <g class=""pawR"" style=""transform-origin:130px 178px""><ellipse cx=""130"" cy=""178"" rx=""13"" ry=""8"" fill=""#c7b49a"" stroke=""#a3907a"" stroke-width=""2""/></g>
    <g class=""head"" style=""transform-origin:100px 96px"">
      <polygon points=""62,64 38,14 96,44"" fill=""#d3c3ac"" stroke=""#a3907a"" stroke-width=""2""/>
      <polygon points=""138,64 162,14 104,44"" fill=""#d3c3ac"" stroke=""#a3907a"" stroke-width=""2""/>
      <polygon points=""66,56 50,28 88,46"" fill=""#f0c9c4""/>
      <polygon points=""134,56 150,28 112,46"" fill=""#f0c9c4""/>
      <circle cx=""100"" cy=""94"" r=""38"" fill=""url(#furDevon)"" stroke=""#b9a58d"" stroke-width=""2""/>
      <path d=""M78 82 q6 6 12 0 M110 82 q6 6 12 0 M86 118 q6 5 12 0"" stroke=""#b9a58d"" stroke-width=""1.5"" fill=""none"" stroke-linecap=""round""/>
      <ellipse class=""eyeOpen"" cx=""84"" cy=""90"" rx=""8"" ry=""10"" fill=""#5a8f7a""/>
      <ellipse class=""eyeOpen"" cx=""116"" cy=""90"" rx=""8"" ry=""10"" fill=""#5a8f7a""/>
      <ellipse cx=""86"" cy=""86"" rx=""2.4"" ry=""3"" fill=""#fff""/><ellipse cx=""118"" cy=""86"" rx=""2.4"" ry=""3"" fill=""#fff""/>
      <path class=""eyeClosed"" d=""M76 91 Q84 97 92 91"" stroke=""#7d6a54"" stroke-width=""3"" fill=""none"" stroke-linecap=""round""/>
      <path class=""eyeClosed"" d=""M108 91 Q116 97 124 91"" stroke=""#7d6a54"" stroke-width=""3"" fill=""none"" stroke-linecap=""round""/>
      <path d=""M96 104 L104 104 L100 109 Z"" fill=""#d99a97""/>
      <g class=""mouth"" style=""transform-origin:100px 112px"">
        <path d=""M100 109 Q94 116 88 112"" stroke=""#7d6a54"" stroke-width=""2"" fill=""none"" stroke-linecap=""round""/>
        <path d=""M100 109 Q106 116 112 112"" stroke=""#7d6a54"" stroke-width=""2"" fill=""none"" stroke-linecap=""round""/>
      </g>
      <path d=""M60 98 L46 94 M60 104 L46 104"" stroke=""#b9a58d"" stroke-width=""1.5"" stroke-linecap=""round""/>
      <path d=""M140 98 L154 94 M140 104 L154 104"" stroke=""#b9a58d"" stroke-width=""1.5"" stroke-linecap=""round""/>
    </g>
  </g>
</svg>
<div id=""crown"">👑</div>
<div id=""sign""><span id=""signText"" style=""display:block;overflow:hidden;text-overflow:ellipsis;""></span></div>
<div id=""fx""></div>
<div id=""demo"" class=""hidden"">
  <button onclick=""act('song','示例歌曲','测试观众')"">点歌</button>
  <button onclick=""act('beat')"">歌词/节拍</button>
  <button onclick=""act('gift')"">礼物</button>
  <button onclick=""act('up')"">上舰</button>
  <button onclick=""act('wave')"">招爪</button>
  <button onclick=""act('hop')"">跳</button>
  <button onclick=""act('shake')"">抖动</button>
  <button onclick=""act('pounce')"">扑击</button>
  <button onclick=""act('dead')"">装死</button>
  <button onclick=""act('sing')"">唱歌</button>
  <button onclick=""reset()"">重置</button>
  <div class=""sep""></div>
  <button onclick=""setRig('siamese')"">暹罗</button>
  <button onclick=""setRig('dachshund')"">腊肠</button>
  <button onclick=""setRig('devon')"">德文</button>
</div>
<script>
(function(){
  var pet=document.getElementById('pet');
  var sign=document.getElementById('sign');
  var signText=document.getElementById('signText');
  var fx=document.getElementById('fx');
  var crown=document.getElementById('crown');

  function once(cls, ms){
    pet.classList.remove(cls);
    void pet.offsetWidth;
    pet.classList.add(cls);
    setTimeout(function(){ pet.classList.remove(cls); }, ms);
  }
  function spawn(symbols, count){
    for(var i=0;i<count;i++){
      var el=document.createElement('div');
      el.className='heart';
      el.textContent=symbols[Math.floor(Math.random()*symbols.length)];
      el.style.left=(35+Math.random()*30)+'%';
      el.style.bottom=(30+Math.random()*25)+'%';
      el.style.animationDelay=(Math.random()*0.35)+'s';
      fx.appendChild(el);
      setTimeout(function(){ el.remove(); }, 2200);
    }
  }
  function act(name, title, requester){
    if(name==='song'){
      signText.textContent='点歌：'+(title||'')+(requester?(' — '+requester):'');
      sign.classList.add('on');
      setTimeout(function(){ sign.classList.remove('on'); }, 3200);
      pet.classList.add('tilt'); setTimeout(function(){ pet.classList.remove('tilt'); }, 1600);
      spawn(['🎵','🎶'],3);
    }else if(name==='beat'){
      if(pet.classList.contains('dead')) return;
      once('hop',500); once('sing',1100);
    }else if(name==='gift'){ once('hop',500); spawn(['❤️','💛','✨','💫'],10); }
    else if(name==='up'){
      once('hop',600);
      crown.classList.add('on'); setTimeout(function(){ crown.classList.remove('on'); },2600);
      spawn(['🎉','✨','💖'],16);
    }
    else if(name==='wave'){ once('wave',1600); }
    else if(name==='hop'){ once('hop',500); }
    else if(name==='shake'){ once('shake',500); }
    else if(name==='pounce'){ once('pounce',600); }
    else if(name==='sing'){ once('sing',1200); }
    else if(name==='dead'){ pet.classList.add('dead'); }
    else if(name==='glow'){ once('glow',1200); }
  }
  function reset(){
    pet.className='idle';
    sign.classList.remove('on'); crown.classList.remove('on'); fx.innerHTML='';
  }
  window.act=act; window.reset=reset;

  // 三套形象切换
  var rigs=['siamese','dachshund','devon'];
  function setRig(name){
    if(rigs.indexOf(name)<0) name='siamese';
    for(var i=0;i<rigs.length;i++){
      var el=document.getElementById('rig-'+rigs[i]);
      if(el) el.setAttribute('class','rig'+(rigs[i]===name?' on':''));
    }
    pet.setAttribute('data-rig', name);
  }
  window.setRig=setRig;
  var forced=/[?&]pet=(siamese|dachshund|devon)/.exec(location.search);
  var initial=forced;
  setRig(initial?initial[1]:'siamese');

  (function blinkLoop(){
    setTimeout(function(){
      pet.classList.add('blink');
      setTimeout(function(){ pet.classList.remove('blink'); },130);
      blinkLoop();
    }, 2200+Math.random()*3800);
  })();

  // 驱动：跟随应用当前主题与真实播放事件
  var lastTitle='', lastVersion=-1, lastToast=0, playing=false, lastTheme='';
  function poll(){
    fetch('/lyrics/state?t='+Date.now()).then(function(r){return r.json();}).then(function(st){
      var theme=st.theme||'';
      if(!forced && theme && theme!==lastTheme && rigs.indexOf(theme)>=0){ lastTheme=theme; setRig(theme); }
      var title=st.title||'';
      if(title && title!==lastTitle){ lastTitle=title; act('song', title, st.requester||''); }
      var v=Number(st.version);
      if(isFinite(v) && v!==lastVersion){ if(lastVersion>=0) act('beat'); lastVersion=v; }
      if(st.toastId && st.toastId!==lastToast){ lastToast=st.toastId; act('gift'); }
      playing=!!st.playing;
      pet.classList.toggle('idle', playing);
    }).catch(function(){}).then(function(){ setTimeout(poll, 700); });
  }
  poll();

  if(location.search.indexOf('demo=1')>=0){ document.getElementById('demo').classList.remove('hidden'); }
  var m=/[?&]act=(\w+)/.exec(location.search);
  if(m) setTimeout(function(){ act(m[1]); }, 700);
})();
</script>
</body>
</html>";
}
