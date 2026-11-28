// Trang web iTest: chuyển tab hình động, đồng hồ đếm ngược minh hoạ, hiệu ứng hiện dần khi cuộn.
(function () {
  'use strict';
  var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  // ---- Hình động giao diện: ba tab tự luân phiên, dừng khi người xem tự chọn hoặc rê chuột vào ----
  var demo = document.querySelector('[data-demo]');
  if (demo) {
    var tabs = Array.prototype.slice.call(demo.querySelectorAll('[data-tab]'));
    var screens = Array.prototype.slice.call(demo.querySelectorAll('[data-screen]'));
    var current = 0, auto = !reduce, hover = false;
    var show = function (i) {
      current = (i + tabs.length) % tabs.length;
      tabs.forEach(function (t, k) { t.classList.toggle('on', k === current); t.setAttribute('aria-selected', k === current ? 'true' : 'false'); });
      screens.forEach(function (s, k) { s.classList.toggle('on', k === current); });
    };
    tabs.forEach(function (t, i) { t.addEventListener('click', function () { auto = false; show(i); }); });
    demo.addEventListener('mouseenter', function () { hover = true; });
    demo.addEventListener('mouseleave', function () { hover = false; });
    setInterval(function () { if (auto && !hover && !document.hidden) { show(current + 1); } }, 9000);
  }

  // ---- Đồng hồ đếm ngược và số câu đã làm trong hình minh hoạ ----
  if (!reduce) {
    var clocks = document.querySelectorAll('[data-clock]');
    var counts = document.querySelectorAll('[data-count]');
    var tick = 0;
    setInterval(function () {
      tick++;
      clocks.forEach(function (c) {
        var s = Math.max(0, parseInt(c.getAttribute('data-clock'), 10) - 1);
        if (s === 0) { s = 2700; }
        c.setAttribute('data-clock', String(s));
        c.textContent = String(Math.floor(s / 60)).padStart(2, '0') + ':' + String(s % 60).padStart(2, '0');
      });
      if (tick % 4 === 0) {
        counts.forEach(function (c) {
          var n = parseInt(c.getAttribute('data-count'), 10);
          n = n >= 40 ? 24 : n + 1;
          c.setAttribute('data-count', String(n));
          c.textContent = String(n);
        });
      }
    }, 1000);
  }

  // ---- Hiện dần khi cuộn tới ----
  var items = document.querySelectorAll('.feat, .flow li, .band div, .faq details, .demo');
  if ('IntersectionObserver' in window && !reduce) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (en) { if (en.isIntersecting) { en.target.classList.add('in'); io.unobserve(en.target); } });
    }, { threshold: 0.12 });
    items.forEach(function (el) { el.classList.add('reveal'); io.observe(el); });
  }
})();
