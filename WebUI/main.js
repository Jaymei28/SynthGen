import './style.css';

document.addEventListener('DOMContentLoaded', () => {
    // Make range sliders update their value display when changed
    const sliders = document.querySelectorAll('input[type="range"]');

    sliders.forEach(slider => {
        slider.addEventListener('input', (e) => {
            const val = e.target.value;
            // Find closest parent group
            const group = e.target.closest('.form-group');
            if (group) {
                const display = group.querySelector('.value-display');
                if (display) {
                    // Quick check to format based on slider type
                    if (val.includes('.') || e.target.step === "0.1" || e.target.step === "0.05") {
                        display.textContent = parseFloat(val).toFixed(2);
                    } else if (e.target.max == 360) {
                        display.textContent = val + '°';
                    } else {
                        display.textContent = val;
                    }
                }
            }

            // Update green fill
            if (e.target.classList.contains('c-green') || true) {
                const min = e.target.min || 0;
                const max = e.target.max || 100;
                const percent = ((val - min) / (max - min)) * 100;
                e.target.style.backgroundSize = `${percent}% 100%`;
            }
        });

        // Initialize background size
        const min = slider.min || 0;
        const max = slider.max || 100;
        const percent = ((slider.value - min) / (max - min)) * 100;
        slider.style.backgroundSize = `${percent}% 100%`;
    });

    // Segmented control click handlers
    const segmentedButtons = document.querySelectorAll('.segmented-control button');
    segmentedButtons.forEach(btn => {
        btn.addEventListener('click', (e) => {
            const parent = e.target.closest('.segmented-control');
            const btns = parent.querySelectorAll('button');
            btns.forEach(b => b.classList.remove('active'));
            const clicked = e.target.closest('button');
            if (clicked) clicked.classList.add('active');
        });
    });
});
