using System.Text;

namespace MusicToppin;

/// <summary>
/// 开发分支原型：手写 SVG 分层宠物 + 全套动作，用于验证交互（不并入 main、不发版）。
/// 访问 /pets 使用；?demo=1 显示动作按钮；透明背景，可直接当浏览器源。
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
  #pet.idle #tail{animation:wag 2.8s ease-in-out infinite;}
  #pet.hop{animation:hop .45s cubic-bezier(.3,1.6,.4,1);}
  #pet.shake{animation:shake .45s ease-in-out;}
  #pet.pounce{animation:pounce .5s cubic-bezier(.2,1.5,.4,1);}
  #pet.dead{animation:dead .8s ease forwards;}
  #pet.tilt #head{transform:rotate(-9deg);transition:transform .35s ease;}
  #pet.tilt #head{transform-origin:100px 96px;}
  #pet.sing #mouth{animation:sing .35s ease-in-out 3;}
  #pet.glow{filter:drop-shadow(0 0 10px rgba(143,184,255,.85));}
  #pet #head{transition:transform .35s ease;transform-origin:100px 96px;}
  #pet #pawR{transform-origin:148px 152px;transition:transform .25s ease;}
  #pet.wave #pawR{animation:wave .5s ease-in-out 3;}
  #pet #tail{transform-origin:44px 150px;}
  .blink #eyeOpenL,.blink #eyeOpenR{visibility:hidden;}
  .blink #eyeClosedL,.blink #eyeClosedR{visibility:visible;}
  #eyeClosedL,#eyeClosedR{visibility:hidden;}
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
  #demo{position:absolute;left:8px;top:8px;display:flex;flex-wrap:wrap;gap:6px;max-width:60%;}
  #demo button{font-size:12px;padding:4px 8px;border-radius:6px;border:1px solid rgba(255,255,255,.25);background:rgba(14,17,23,.8);color:#eaf2ff;cursor:pointer;}
  .hidden{display:none!important;}
</style>
</head>
<body>
<svg id=""pet"" viewBox=""0 0 200 200"" class=""idle"" xmlns=""http://www.w3.org/2000/svg"">
  <defs>
    <linearGradient id=""fur"" x1=""0"" y1=""0"" x2=""0"" y2=""1"">
      <stop offset=""0"" stop-color=""#f4e6d0""/><stop offset=""1"" stop-color=""#e3c9a6""/>
    </linearGradient>
    <radialGradient id=""cheek"" cx=""0.5"" cy=""0.5"" r=""0.5"">
      <stop offset=""0"" stop-color=""#ffd9c2"" stop-opacity=""0.9""/><stop offset=""1"" stop-color=""#ffd9c2"" stop-opacity=""0""/>
    </radialGradient>
  </defs>
  <ellipse id=""shadow"" cx=""100"" cy=""190"" rx=""58"" ry=""12"" fill=""rgba(0,0,0,.22)""/>
  <g id=""tail"">
    <path d=""M48 158 C20 150 12 120 26 104 C34 95 48 98 46 110 C44 120 34 124 34 134 C34 146 44 150 52 152 Z"" fill=""#d9b98f"" stroke=""#b8946a"" stroke-width=""2""/>
  </g>
  <g id=""body"">
    <ellipse cx=""100"" cy=""150"" rx=""54"" ry=""38"" fill=""url(#fur)"" stroke=""#c9a87c"" stroke-width=""2""/>
    <ellipse cx=""100"" cy=""160"" rx=""34"" ry=""24"" fill=""#fff6ea"" opacity=""0.85""/>
  </g>
  <g id=""pawL""><ellipse cx=""64"" cy=""178"" rx=""15"" ry=""10"" fill=""#f4e6d0"" stroke=""#c9a87c"" stroke-width=""2""/></g>
  <g id=""pawR""><ellipse cx=""136"" cy=""178"" rx=""15"" ry=""10"" fill=""#f4e6d0"" stroke=""#c9a87c"" stroke-width=""2""/></g>
  <g id=""head"">
    <polygon id=""earL"" points=""66,60 58,18 92,44"" fill=""#e3c9a6"" stroke=""#b8946a"" stroke-width=""2""/>
    <polygon id=""earR"" points=""134,60 142,18 108,44"" fill=""#e3c9a6"" stroke=""#b8946a"" stroke-width=""2""/>
    <polygon points=""68,54 64,30 84,46"" fill=""#f7c6c0""/>
    <polygon points=""132,54 136,30 116,46"" fill=""#f7c6c0""/>
    <circle cx=""100"" cy=""92"" r=""44"" fill=""url(#fur)"" stroke=""#c9a87c"" stroke-width=""2""/>
    <circle cx=""72"" cy=""102"" r=""12"" fill=""url(#cheek)""/>
    <circle cx=""128"" cy=""102"" r=""12"" fill=""url(#cheek)""/>
    <ellipse id=""eyeOpenL"" cx=""84"" cy=""86"" rx=""6.5"" ry=""8"" fill=""#3d4654""/>
    <ellipse id=""eyeOpenR"" cx=""116"" cy=""86"" rx=""6.5"" ry=""8"" fill=""#3d4654""/>
    <path id=""eyeClosedL"" d=""M77 87 Q84 93 91 87"" stroke=""#3d4654"" stroke-width=""3"" fill=""none"" stroke-linecap=""round""/>
    <path id=""eyeClosedR"" d=""M109 87 Q116 93 123 87"" stroke=""#3d4654"" stroke-width=""3"" fill=""none"" stroke-linecap=""round""/>
    <ellipse id=""eyeHiL"" cx=""86"" cy=""83"" rx=""2"" ry=""2.6"" fill=""#fff""/>
    <ellipse id=""eyeHiR"" cx=""118"" cy=""83"" rx=""2"" ry=""2.6"" fill=""#fff""/>
    <path d=""M96 100 L104 100 L100 105 Z"" fill=""#e08a8a""/>
    <g id=""mouth"" transform-origin=""100px 108px"">
      <path d=""M100 105 Q94 112 88 108"" stroke=""#8d6e52"" stroke-width=""2"" fill=""none"" stroke-linecap=""round""/>
      <path d=""M100 105 Q106 112 112 108"" stroke=""#8d6e52"" stroke-width=""2"" fill=""none"" stroke-linecap=""round""/>
    </g>
    <path d=""M58 92 L44 88 M58 98 L44 98"" stroke=""#b8946a"" stroke-width=""1.6"" stroke-linecap=""round""/>
    <path d=""M142 92 L156 88 M142 98 L156 98"" stroke=""#b8946a"" stroke-width=""1.6"" stroke-linecap=""round""/>
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
</div>
<script>
(function(){
  var pet=document.getElementById('pet');
  var sign=document.getElementById('sign');
  var signText=document.getElementById('signText');
  var fx=document.getElementById('fx');
  var crown=document.getElementById('crown');
  var lastAct='';

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
    lastAct=name;
    if(name==='song'){
      signText.textContent='点歌：'+(title||'')+(requester?(' — '+requester):'');
      sign.classList.add('on');
      setTimeout(function(){ sign.classList.remove('on'); }, 3200);
      pet.classList.add('tilt'); setTimeout(function(){ pet.classList.remove('tilt'); }, 1600);
      spawn(['🎵','🎶'],3);
    }else if(name==='beat'){
      if(pet.classList.contains('dead')) return;
      once('hop',500); once('sing',1100);
    }else if(name==='gift'){
      once('hop',500); spawn(['❤️','💛','✨','💫'],10);
    }else if(name==='up'){
      once('hop',600);
      crown.classList.add('on'); setTimeout(function(){ crown.classList.remove('on'); },2600);
      spawn(['🎉','✨','💖'],16);
    }else if(name==='wave'){ once('wave',1600); }
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

  // 眨眼：随机间隔
  (function blinkLoop(){
    setTimeout(function(){
      pet.classList.add('blink');
      setTimeout(function(){ pet.classList.remove('blink'); },130);
      blinkLoop();
    }, 2200+Math.random()*3800);
  })();

  // 驱动：/lyrics/state 的真实播放事件
  var lastTitle='', lastVersion=-1, lastToast=0, playing=false;
  function poll(){
    fetch('/lyrics/state?t='+Date.now()).then(function(r){return r.json();}).then(function(st){
      var title=st.title||'';
      if(title && title!==lastTitle){
        lastTitle=title;
        if(st.style===3||st.style===0||st.style===1||st.style===2) act('song', title, st.requester||'');
      }
      var v=Number(st.version);
      if(isFinite(v) && v!==lastVersion){
        if(lastVersion>=0) act('beat');
        lastVersion=v;
      }
      if(st.toastId && st.toastId!==lastToast){ lastToast=st.toastId; act('gift'); }
      playing=!!st.playing;
      document.getElementById('pet').classList.toggle('idle', playing);
    }).catch(function(){}).then(function(){ setTimeout(poll, 700); });
  }
  poll();

  if(location.search.indexOf('demo=1')>=0){
    document.getElementById('demo').classList.remove('hidden');
  }
  var m=/[?&]act=(\w+)/.exec(location.search);
  if(m) setTimeout(function(){ act(m[1]); }, 700);
})();
</script>
</body>
</html>";
}
