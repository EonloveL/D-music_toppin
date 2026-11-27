using System.Text;

namespace MusicToppin;

/// <summary>
/// 开发分支原型：PNG 立绘 + 程序动画/粒子特效的宠物互动层。
/// 访问 /pets 使用；透明背景，可直接当浏览器源；?demo=1 显示动作按钮，?pet= 强制形象，?act= 触发动作。
/// </summary>
internal static class PetOverlay
{
    public const string Page = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<title>Music Toppin · Pet</title>
<style>
  html,body{margin:0;padding:0;width:100%;height:100%;background:transparent;overflow:hidden;font-family:'Microsoft YaHei UI',sans-serif;}
  #pet{position:absolute;left:50%;bottom:2%;transform:translateX(-50%);transform-origin:50% 92%;
    display:flex;align-items:flex-end;justify-content:center;pointer-events:none;}
  #pet img{display:block;height:min(86vh,86vw);max-height:520px;width:auto;
    filter:drop-shadow(0 8px 18px rgba(0,0,0,.35));transform-origin:50% 95%;
    transition:filter .25s ease;}
  #pet.idle img{animation:breathe 3.8s ease-in-out infinite;}
  #pet.tilt img{transform:rotate(-8deg) translateY(3px);}
  #pet.glow img{filter:drop-shadow(0 0 18px rgba(143,184,255,.95)) drop-shadow(0 8px 18px rgba(0,0,0,.35));}
  #pet.hop{animation:hop .5s cubic-bezier(.3,1.5,.4,1);}
  #pet.beat{animation:beat .45s ease-in-out;}
  #pet.shake{animation:shake .45s ease-in-out;}
  #pet.pounce{animation:pounce .55s cubic-bezier(.2,1.5,.4,1);}
  #pet.dead{animation:dead .8s ease forwards;}
  #pet.dead img{filter:grayscale(.4) drop-shadow(0 8px 18px rgba(0,0,0,.35));}
  @keyframes breathe{0%,100%{transform:scale(1,1) translateY(0);}50%{transform:scale(1.012,1.018) translateY(-3px);}}
  @keyframes hop{0%,100%{transform:translate(-50%,0);}40%{transform:translate(-50%,-26px) scale(1.02);}}
  @keyframes beat{0%,100%{transform:translate(-50%,0) scale(1);}45%{transform:translate(-50%,-10px) scale(1.02);}}
  @keyframes shake{0%,100%{transform:translate(-50%,0);}20%{transform:translate(-53%,0) rotate(-3deg);}60%{transform:translate(-47%,0) rotate(3deg);}}
  @keyframes pounce{0%{transform:translate(-50%,0) scale(1);}35%{transform:translate(-50%,-32px) scale(1.06) rotate(-5deg);}70%{transform:translate(-50%,6px) scale(.98);}100%{transform:translate(-50%,0);}}
  @keyframes dead{to{transform:translate(-50%,26px) rotate(76deg);opacity:.8;}}
  #sign{position:absolute;left:50%;bottom:78%;transform:translateX(-50%);background:rgba(14,17,23,.92);border:1px solid rgba(255,255,255,.18);
    border-radius:12px;padding:8px 14px;color:#eaf2ff;font-size:16px;white-space:nowrap;opacity:0;transition:opacity .2s;pointer-events:none;max-width:88%;overflow:hidden;text-overflow:ellipsis;}
  #sign.on{opacity:1;}
  #fx{position:absolute;inset:0;pointer-events:none;overflow:hidden;}
  .heart,.star,.confetti{position:absolute;font-size:22px;animation:floatUp 1.6s ease-out forwards;}
  @keyframes floatUp{0%{transform:translateY(0) scale(.7);opacity:0}20%{opacity:1}100%{transform:translateY(-160px) scale(1.1);opacity:0}}
  #crown{position:absolute;left:50%;top:6%;transform:translateX(-50%);font-size:36px;opacity:0;transition:opacity .2s;}
  #crown.on{opacity:1;}
  #demo{position:absolute;left:8px;top:8px;display:flex;flex-wrap:wrap;gap:6px;max-width:92%;}
  #demo button{font-size:12px;padding:4px 8px;border-radius:6px;border:1px solid rgba(255,255,255,.25);background:rgba(14,17,23,.8);color:#eaf2ff;cursor:pointer;}
  #demo .sep{width:100%;height:0;}
  .hidden{display:none!important;}
</style>
</head>
<body>
<div id=""pet"" class=""idle""><img id=""petImg"" alt="""" /></div>
<div id=""crown"">👑</div>
<div id=""sign""><span id=""signText"" style=""display:block;overflow:hidden;text-overflow:ellipsis;""></span></div>
<div id=""fx""></div>
<div id=""demo"" class=""hidden"">
  <button onclick=""act('song','示例歌曲','测试观众')"">点歌</button>
  <button onclick=""act('beat')"">歌词/节拍</button>
  <button onclick=""act('gift')"">礼物</button>
  <button onclick=""act('up')"">上舰</button>
  <button onclick=""act('hop')"">跳</button>
  <button onclick=""act('tilt')"">歪头</button>
  <button onclick=""act('shake')"">抖动</button>
  <button onclick=""act('pounce')"">扑击</button>
  <button onclick=""act('dead')"">装死</button>
  <button onclick=""act('glow')"">发光</button>
  <button onclick=""reset()"">重置</button>
  <div class=""sep""></div>
  <button onclick=""setPet('siamese')"">暹罗</button>
  <button onclick=""setPet('dachshund')"">腊肠</button>
  <button onclick=""setPet('devon')"">德文</button>
</div>
<script>
(function(){
  var pet=document.getElementById('pet');
  var img=document.getElementById('petImg');
  var sign=document.getElementById('sign');
  var signText=document.getElementById('signText');
  var fx=document.getElementById('fx');
  var crown=document.getElementById('crown');

  var themes={siamese:'/assets/themes/siamese.png?v=150',dachshund:'/assets/themes/dachshund.png?v=150',devon:'/assets/themes/devon.png?v=150'};
  var current='';

  function once(cls, ms){
    pet.classList.remove(cls);
    void pet.offsetWidth;
    pet.classList.add(cls);
    setTimeout(function(){ pet.classList.remove(cls); }, ms);
  }
  function spawn(symbols, count, cls){
    for(var i=0;i<count;i++){
      var el=document.createElement('div');
      el.className=cls||'heart';
      el.textContent=symbols[Math.floor(Math.random()*symbols.length)];
      el.style.left=(30+Math.random()*40)+'%';
      el.style.bottom=(34+Math.random()*26)+'%';
      el.style.animationDelay=(Math.random()*0.35)+'s';
      fx.appendChild(el);
      setTimeout(function(){ el.remove(); }, 2200);
    }
  }
  function setPet(name){
    if(!themes[name]) name='siamese';
    current=name;
    img.src=themes[name];
  }
  function reset(){
    pet.className='idle';
    sign.classList.remove('on'); crown.classList.remove('on'); fx.innerHTML='';
  }
  function act(name, title, requester){
    if(name==='song'){
      signText.textContent='点歌：'+(title||'')+(requester?(' — '+requester):'');
      sign.classList.add('on');
      setTimeout(function(){ sign.classList.remove('on'); }, 3200);
      pet.classList.add('tilt'); setTimeout(function(){ pet.classList.remove('tilt'); }, 1800);
      spawn(['🎵','🎶'],3,'star');
    }else if(name==='beat'){ if(pet.classList.contains('dead')) return; once('beat',500); }
    else if(name==='gift'){ once('hop',500); spawn(['❤️','💛','✨','💫'],10,'heart'); }
    else if(name==='up'){ once('hop',600); crown.classList.add('on'); setTimeout(function(){ crown.classList.remove('on'); },2600); spawn(['🎉','✨','💖'],16,'confetti'); }
    else if(name==='hop'){ once('hop',500); }
    else if(name==='tilt'){ pet.classList.add('tilt'); setTimeout(function(){ pet.classList.remove('tilt'); },1800); }
    else if(name==='shake'){ once('shake',500); }
    else if(name==='pounce'){ once('pounce',600); }
    else if(name==='dead'){ pet.classList.add('dead'); }
    else if(name==='glow'){ once('glow',1400); }
  }
  window.act=act; window.reset=reset; window.setPet=setPet;

  var forced=/[?&]pet=(siamese|dachshund|devon)/.exec(location.search);
  setPet(forced?forced[1]:'siamese');

  // 跟随应用主题与真实播放事件
  var lastTitle='', lastVersion=-1, lastToast=0, lastTheme='';
  function poll(){
    fetch('/lyrics/state?t='+Date.now()).then(function(r){return r.json();}).then(function(st){
      var theme=st.theme||'';
      if(!forced && theme && theme!==lastTheme && themes[theme]){ lastTheme=theme; setPet(theme); }
      var title=st.title||'';
      if(title && title!==lastTitle){ lastTitle=title; act('song', title, st.requester||''); }
      var v=Number(st.version);
      if(isFinite(v) && v!==lastVersion){ if(lastVersion>=0) act('beat'); lastVersion=v; }
      if(st.toastId && st.toastId!==lastToast){ lastToast=st.toastId; act('gift'); }
      pet.classList.toggle('idle', !!st.playing);
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
