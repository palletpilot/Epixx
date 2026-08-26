async function submitTransfer(type, storeId) {
    const datePicker = document.getElementById("date-picker-for-transfer");

    const transfers = [];

    document.querySelectorAll(".transfer-field").forEach(field => {
        const transferDescription =
            field.querySelector(".transfer-description").textContent.trim();

        const transferQty =
            Number(field.querySelector(".qty-input").value);

        if (transferQty <= 0) {
            return;
        }

        transfers.push({
            description: transferDescription,
            count: transferQty,
            transferDate: datePicker.value
        });
    });

    if (transfers.length === 0) {
        showNotification("Inga pallar var valda!", "error");
        return;
    }

    const response = await fetch(
        `/Admin/CreateTransfer?type=${encodeURIComponent(type)}&storeId=${encodeURIComponent(storeId)}`,
        {
            method: "POST",
            headers: {
                "Content-Type": "application/json"
            },
            body: JSON.stringify(transfers)
        }
    );

    if (!response.ok) {
        showNotification(
            "Något gick fel när transfers skulle skapas.",
            "error"
        );
        return;
    }

    const result = await response.json();

    if (result.success) {
        document.querySelectorAll(".qty-input").forEach(input => {
            input.value = 0;
        });

        const totalPallets = transfers.reduce(
            (sum, transfer) => sum + transfer.count,
            0
        );

        showNotification(
            `Lade till ${totalPallets} pallar till datumet ${datePicker.value}`,
            "success"
        );

        await updateUIForDateChange(
            datePicker.value,
            type,
            storeId
        );

        await updateAvailablePallets();
    }
}
function updateButtonState() {
    const qtyInputs = document.querySelectorAll(".qty-input");
    const submitBtn = document.getElementById("submitBtn");
    let isZero = true;
    for (const input of qtyInputs) {
        if (Number(input.value) !== 0) {
            isZero = false;
            break;
        }
    }

    submitBtn.disabled = isZero;
}

async function updateAvailablePallets() {
    const response = await fetch("/Admin/GetAvailablePallets");

    if (!response.ok) {
        console.error("Kunde inte hämta tillgängliga pallar.");
        return;
    }

    const data = await response.json();

    const tbody = document.getElementById("available-pallets-body");

    tbody.innerHTML = "";
    data.forEach((item, index) => {

        const row = document.createElement("tr");
        row.classList.add("transfer-field");

        row.innerHTML = `
            <td class="transfer-description">${item.description}</td>
            <td>${item.count}</td>
            <td>
                <input type="number"
                       min="0"
                       max="${item.count}"
                       value="0"
                       data-max="${item.count}"
                       class="qty-input" />
            </td>
        `;

        tbody.appendChild(row);
    });

    // Eftersom inputs skapades på nytt måste event listeners kopplas igen
    document.querySelectorAll(".qty-input").forEach(input => {
        input.addEventListener("input", updateButtonState);
    });

    updateButtonState();

}

async function updateUIForDateChange(selectedDate, type, storeId) {
    const tbody = document.getElementById("tbody-for-day"); 

    const response = await fetch(`/Admin/GetPalletsForDate?date=${selectedDate}&type=${type}&storeId=${storeId}`);
    if (response.ok) {
        const data = await response.json();
        tbody.innerHTML = "";
        if (data.length === 0) {
            const row = document.createElement("tr");
            const noDataCell = document.createElement("td");
            if (type === "Awaiting PalletTransfer") {
                noDataCell.textContent = `Inga omflytt till plock har beordrats än!`;
            } else if (type === "Awaiting PackingAreaTransfer") {
                noDataCell.textContent = `Inga omflytt till packyta har beordrats än!`;
            } else {
                alert("Ej implementerat än!");
            }
            row.appendChild(noDataCell);
            tbody.appendChild(row);
            return;
        }
        data.forEach(item => {
            const row = document.createElement("tr");
            const descriptionCell = document.createElement("td");
            descriptionCell.textContent = item.description;
            const countCell = document.createElement("td");
            countCell.textContent = item.count;
            row.appendChild(descriptionCell);
            row.appendChild(countCell);
            tbody.appendChild(row);
        });
    }
}
document.addEventListener("DOMContentLoaded", function () {
    const page = document.querySelector(".double-table-container");

    if (!page) {
        return;
    }

    const type = page.dataset.type;
    const submitBtn = document.getElementById("submitBtn");
    const datePicker = document.getElementById("date-picker-for-transfer");
    const storePicker = document.getElementById("store-picker");

    if (!submitBtn || !datePicker) {
        return;
    }

    const today = new Date().toISOString().split("T")[0];

    datePicker.value = today;

    function getStoreId() {
        return storePicker ? storePicker.value : -1;
    }

    updateUIForDateChange(
        today,
        type,
        getStoreId()
    );

    submitBtn.addEventListener("click", async function () {
        await submitTransfer(
            type,
            getStoreId()
        );
    });

    datePicker.addEventListener("change", async function () {
        await updateUIForDateChange(
            this.value,
            type,
            getStoreId()
        );
    });

    if (storePicker) {
        storePicker.addEventListener("change", async function () {
            await updateUIForDateChange(
                datePicker.value,
                type,
                this.value
            );
        });
    }

    document.addEventListener("input", function (event) {
        if (event.target.classList.contains("qty-input")) {
            updateButtonState();
        }
    });

    updateButtonState();
});
