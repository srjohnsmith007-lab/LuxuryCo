/* =====================================================
   LUXURYCO — Global Initialization
   Loaded on every page via _Layout.cshtml
   ===================================================== */

document.addEventListener('DOMContentLoaded', function () {

    // ── GSAP ScrollTrigger: [data-reveal] + [data-parallax] ──
    if (typeof gsap !== 'undefined' && typeof ScrollTrigger !== 'undefined') {
        gsap.registerPlugin(ScrollTrigger);

        document.querySelectorAll('[data-reveal]').forEach(el => {
            const direction = el.dataset.reveal || 'up';
            const delay = parseFloat(el.dataset.revealDelay) || 0;
            const fromVars = { opacity: 0, duration: 0.8, delay, ease: 'power3.out' };

            if (direction === 'up') fromVars.y = 40;
            else if (direction === 'down') fromVars.y = -40;
            else if (direction === 'left') fromVars.x = 40;
            else if (direction === 'right') fromVars.x = -40;
            else if (direction === 'scale') fromVars.scale = 0.92;

            gsap.from(el, {
                ...fromVars,
                scrollTrigger: { trigger: el, start: 'top 88%', once: true }
            });
        });

        document.querySelectorAll('[data-parallax]').forEach(el => {
            const speed = parseFloat(el.dataset.parallax) || 0.3;
            gsap.to(el, {
                y: () => speed * 100,
                ease: 'none',
                scrollTrigger: { trigger: el, start: 'top bottom', end: 'bottom top', scrub: 1 }
            });
        });
    }

    // ── Scroll Progress Bar ──
    window.addEventListener('scroll', () => {
        const winScroll = document.body.scrollTop || document.documentElement.scrollTop;
        const height = document.documentElement.scrollHeight - document.documentElement.clientHeight;
        if (height > 0) {
            const bar = document.getElementById('scroll-progress');
            if (bar) bar.style.width = ((winScroll / height) * 100) + '%';
        }
    });

    // ── Ripple Effect on CTA buttons ──
    document.querySelectorAll('.ripple-btn, .btn-cta-main').forEach(btn => {
        btn.addEventListener('click', function (e) {
            const rect = this.getBoundingClientRect();
            const ripple = document.createElement('span');
            ripple.className = 'ripple';
            ripple.style.left = (e.clientX - rect.left) + 'px';
            ripple.style.top = (e.clientY - rect.top) + 'px';
            this.appendChild(ripple);
            setTimeout(() => ripple.remove(), 600);
        });
    });

    // ── Supabase Password Reset Interceptor ──
    if (window.location.hash && window.location.hash.includes('type=recovery')) {
        window.location.href = '/Account/ResetPassword' + window.location.hash;
    }

    // ── Voice Search Integration ──
    const btnSearchMic = document.getElementById('btn-search-mic');
    const searchMicIcon = document.getElementById('search-mic-icon');
    const globalSearchInput = document.getElementById('global-search-input');
    const globalSearchForm = document.getElementById('global-search-form');

    if (btnSearchMic) {
        btnSearchMic.addEventListener('click', () => {
            if (window.VoiceAssistant && window.VoiceAssistant.state === 'LISTENING') {
                window.VoiceAssistant.stopListening();
            } else if (window.VoiceAssistant) {
                window.VoiceAssistant.startListening(
                    (text) => {
                        globalSearchInput.value = text;
                        globalSearchForm.submit();
                    },
                    (state) => {
                        if (state === 'LISTENING') {
                            searchMicIcon.className = 'fas fa-microphone text-danger';
                        } else if (state === 'PROCESSING') {
                            searchMicIcon.className = 'fas fa-circle-notch fa-spin text-warning';
                        } else {
                            searchMicIcon.className = 'fas fa-microphone';
                        }
                    }
                );
            }
        });
    }
});
